using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using NightmareAutomate.Helpers;
using NightmareAutomate.Models;
using NightmareAutomate.Services;

namespace NightmareAutomate.Strategies;

/// <summary>
/// Simulacrum (Delirium) farm strategy — 15 waves started by clicking the
/// "Afflictionator" NPC-like entity at the start location.
///
/// Combat is externalized to ReAgent: this strategy only positions the player near
/// monsters and hovers them (Render.InteractCenter) so ReAgent's rules fire.
/// Monsters (and the pack-spawner daemons of not-yet-spawned packs) are always in
/// the entity list, so there is no patrolling: mid-wave the bot fights what it
/// sees, walks to spawner daemons to trigger their packs, and otherwise holds at
/// the start area.
///
/// This class is the PHASE MACHINE; the specialist concerns live in composed
/// components: <see cref="AfflictionatorGateway"/> (initiator finding + wave
/// state), <see cref="TargetSelector"/> (scoring, stickiness, stall bench) and
/// <see cref="HuntBehavior"/> (daemon chasing, out-of-bubble hints, shake-off).
///
/// No recorded locations: the only discovered things are the wave initiator, the
/// in-area stash, and the return portal — found by exploring until they stream
/// into entity range and remembered per MAP INSTANCE in AreaMemory (area hash),
/// never across instances or to disk.
/// </summary>
public sealed class SimulacrumStrategy : IFarmStrategy
{
    public const string FragmentPath = "Metadata/Items/MapFragments/CurrencyAfflictionFragment";

    private const int TotalWaves = 15;

    /// <summary>Hideout round trips allowed per run to empty a full inventory when
    /// the in-area stash cannot be opened. Each one costs a portal, and the point
    /// is to break the deadlock, not to shuttle all night.</summary>
    private const int MaxStashTrips = 2;

    /// <summary>Portals that must remain for a stash trip: the trip's re-entry
    /// spends one, and the rest of the run still needs one to survive a death.</summary>
    private const int MinPortalsForStashTrip = 2;

    /// <summary>Never hovered, targeted, or counted as wave presence — Delirium
    /// volatiles explode on their own and must not be chased, and InvisibleAura
    /// carriers ("Banner of Hatred" etc., planted by Lunaris/Solaris Champions)
    /// are untargetable aura pylons that don't count toward the wave.</summary>
    private static readonly string[] IgnoredMonsterPaths =
        ["LeagueAffliction/Volatile", "Monsters/InvisibleAura/"];

    /// <summary>The 5 possible Simulacrum layouts.</summary>
    public static readonly IReadOnlyList<string> AreaNames = new[]
    {
        "Lunacy's Watch",
        "The Bridge Enraptured",
        "The Syndrome Encampment",
        "Hysteriagate",
        "Oriath Delusion"
    };

    private static readonly HashSet<string> AreaNameSet = new(AreaNames, StringComparer.OrdinalIgnoreCase);

    private readonly NavigationService _navigation;
    private readonly GameStateService _gameState;
    private readonly LootService _lootService;
    private readonly InventoryService _inventory;
    private readonly StashService _stashService;
    private readonly ExplorationService _exploration;
    private readonly InputService _input;
    private readonly GemLevelService _gems;
    private readonly AreaMemory _areas;
    private readonly NightmareAutomateSettings _settings;
    private readonly Action<string> _log;

    private readonly AfflictionatorGateway _initiator;
    private readonly TargetSelector _targets;
    private readonly HuntBehavior _hunt;

    /// <summary>
    /// The three discovered positions of the CURRENT map instance: wave initiator,
    /// in-area stash, return portal. Entities stream out with distance, so the last
    /// seen position is kept (in the plugin-owned AreaMemory, so a bot restart
    /// inside the same map keeps them; a new Simulacrum instance has a new hash
    /// and simply reads nothing). Never persisted to disk.
    /// </summary>
    private Vector2? StartPoint => _areas.GetPoint(_gameState.AreaHash, AreaMemory.SimStartKey);

    private Vector2? StashPoint => _areas.GetPoint(_gameState.AreaHash, AreaMemory.SimStashKey);

    private Vector2? PortalPoint
    {
        get => _areas.GetPoint(_gameState.AreaHash, AreaMemory.SimPortalKey);
        set
        {
            if (value is { } p)
                _areas.SetPoint(_gameState.AreaHash, AreaMemory.SimPortalKey, p);
        }
    }

    private SimPhase _phase = SimPhase.MovingToStart;
    private long _lastMonsterSeenAt = Clock.Never;
    private long _waveClickAt = Clock.Never;
    private int _waveClickAttempts;

    /// <summary>When our own click's wave start was counted. The initiator's wave
    /// number lags ~1 s behind its 'active' flag, so for this long after a counted
    /// start its (still previous) number must not pull our count back.</summary>
    private long _waveCountedAt = Clock.Never;
    private const int WaveNumberLagSeconds = 5;
    private int _waveStartReEntries;
    private long _gemLevelAt = Clock.Never;
    private bool _warnedInventoryFull;

    /// <summary>Hideout stash trips taken in this run. The in-area stash flow is
    /// already exhaustive (direct clicks, clicks from range, every approach
    /// angle), so a failure means it is genuinely unusable here — usually a fixed
    /// UI panel drawn over it. The escalation is a portal trip, and it is capped:
    /// once spent, looting stops for the rest of the run rather than looping.</summary>
    private int _stashTripsTaken;

    /// <summary>A hideout stash trip is owed to the engine — set inside the loot
    /// pause, acted on when it unwinds (the engine owns leaving and re-entering).</summary>
    private bool _stashTripRequested;

    /// <summary>The trip budget is spent: stop trying to deposit in this run.</summary>
    private bool _inAreaStashGivenUp;
    private bool _settleDone;
    private bool _sawServerMonstersThisWave;
    private long _lootDivertAt = Clock.Never;

    public SimulacrumStrategy(
        BotServices services,
        NightmareAutomateSettings settings,
        Action<string> log)
    {
        _navigation = services.Navigation;
        _gameState = services.GameState;
        _lootService = services.Loot;
        _inventory = services.Inventory;
        _stashService = services.Stash;
        _exploration = services.Exploration;
        _input = services.Input;
        _gems = services.Gems;
        _areas = services.Areas;
        _settings = settings;
        _log = log;

        _initiator = new AfflictionatorGateway(_gameState, _areas, log);
        _targets = new TargetSelector(_gameState, settings, log);
        _hunt = new HuntBehavior(
            _gameState, _navigation, _exploration, GetNearbyMonsters,
            [.. IgnoredMonsterPaths, AfflictionatorGateway.PathFragment], log);
    }

    public string Name => "Simulacrum";
    public string CurrentPhaseName => _phase.ToString();

    public IReadOnlyList<string> MapDeviceItemPaths { get; } = new[] { FragmentPath };

    public string RestockTabName => _settings.SimulacrumTabName;

    public bool IsFarmArea(string areaName) => AreaNameSet.Contains(areaName);

    public IEnumerable<(string Label, string Value)> GetStatusRows(RunContext run)
    {
        yield return ("Waves", $"{run.WavesStarted}/{TotalWaves}");
    }

    public void Reset()
    {
        _phase = SimPhase.MovingToStart;
        _lastMonsterSeenAt = Clock.Never;
        _waveClickAt = Clock.Never;
        _waveClickAttempts = 0;
        _waveCountedAt = Clock.Never;
        _waveStartReEntries = 0;
        _gemLevelAt = Clock.Never;
        _warnedInventoryFull = false;
        _stashTripsTaken = 0;
        _stashTripRequested = false;
        _inAreaStashGivenUp = false;
        _settleDone = false;
        _sawServerMonstersThisWave = false;
        _lootDivertAt = Clock.Never;
        _monstersCache = null;
        _monstersCachedAt = Clock.Never;
        _lootService.ForgetAll();
        _temporalAuraCache.Clear();
        _initiator.Reset();
        _targets.Reset();
        _hunt.Reset();
    }

    public async SyncTask<bool> ExecuteFarm(RunContext run, CancellationToken ct)
    {
        UpdatePortalKnowledge(run);

        // Hard per-run time limit: some rare mod rolls are effectively unkillable
        // for the build and would pin the bot in one wave for hours. Past the
        // limit the run is not fought any further — skip straight to Finishing,
        // which walks out and reports the run as complete; the engine then
        // opens the next Simulacrum as usual.
        var abandonAfter = _settings.AbandonRunAfterMinutes;
        if (abandonAfter > 0 && !run.AbandonedByTimer &&
            _phase is not (SimPhase.Finishing or SimPhase.Completed) &&
            run.RunStartedAt != 0 &&
            Clock.MsSince(run.RunStartedAt) >= abandonAfter * 60_000L)
        {
            run.AbandonedByTimer = true;
            _log($"[Simulacrum] Run has been going for over {abandonAfter} minutes " +
                 $"(wave {run.WavesStarted}/{TotalWaves}) -- abandoning it and moving on.");
            await _navigation.ReleaseMoveKey();
            _phase = SimPhase.Finishing;
        }

        // Remember every ground item we pass (mid-wave drops etc.) so the
        // post-wave loot phase can collect them even after they leave the bubble.
        _lootService.ObserveGroundLoot();

        _phase = _phase switch
        {
            SimPhase.MovingToStart => await DoMovingToStart(run, ct),
            SimPhase.StartingWave => await DoStartingWave(run, ct),
            SimPhase.Fighting => await DoFighting(run, ct),
            SimPhase.LootPause => await DoLootPause(run, ct),
            SimPhase.Finishing => await DoFinishing(run, ct),
            SimPhase.Completed => SimPhase.Completed,
            _ => SimPhase.Completed
        };

        return _phase == SimPhase.Completed;
    }

    /// <summary>
    /// Shared 15/15 handling ('goodbye' is the authoritative end-of-run signal):
    /// route through the loot pause first (throttled, so unpickable leftovers
    /// can't loop us), then finish. Null while the run is not complete.
    /// </summary>
    private SimPhase? CheckRunComplete(Entity? afflictionator)
    {
        if (_initiator.IsRunComplete(afflictionator) != true) return null;

        if (_lootService.HasKnownLoot() &&
            Clock.SecondsSince(_lootDivertAt) > 30)
        {
            _lootDivertAt = Clock.NowMs;
            _log("[Simulacrum] Run complete -- collecting the remaining loot first.");
            return SimPhase.LootPause;
        }

        BotLog.Step("[Simulacrum] Initiator says goodbye -- run complete.");
        return SimPhase.Finishing;
    }

    private async SyncTask<SimPhase> DoMovingToStart(RunContext run, CancellationToken ct)
    {
        // Give the area a moment to settle after a fresh transition — but never stand
        // around when a wave is already on top of us (bot started mid-wave).
        if (!_settleDone)
        {
            _settleDone = true;
            if (GetNearbyMonsters().Count == 0)
                await Task.Delay(1500, ct);
        }

        // Resuming mid-wave (bot restarted inside the map): monsters are already
        // around us — adopt the running wave right here instead of walking away.
        if (GetNearbyMonsters().Count > 0)
        {
            BotLog.Step("[Simulacrum] Monsters around us -- resuming the running wave.");
            return SimPhase.StartingWave;
        }

        // The initiator only needs to be STREAMED IN to read its state (verified
        // readable at 160+ units) — never walk on top of it just to look at it.
        var aff = _initiator.Find();
        if (aff != null)
            return SimPhase.StartingWave;

        // Not loaded. If the initiator was already discovered in this map instance,
        // walk back toward its remembered position until it streams in.
        if (StartPoint is { } startPoint)
        {
            BotLog.Step($"[Simulacrum] Moving toward the start point {startPoint}...");
            await _navigation.MoveToTarget(startPoint, ct, 90);
            aff = _initiator.Find();

            if (aff == null &&
                Vector2.Distance(_gameState.PlayerGridPos, startPoint) < 100)
            {
                // Close and still nothing — step in further before giving up.
                await _navigation.MoveToTarget(startPoint, ct, 20);
                aff = _initiator.Find();
            }

            if (aff != null)
                return SimPhase.StartingWave;
        }
        else
        {
            // Nothing discovered yet — walk the exploration tour (one leg per
            // phase tick, so the phase machine stays responsive) until the
            // initiator streams into entity range. Throttled: the full-list path
            // scan must not run every frame of the walk.
            await _exploration.WalkTourLeg(
                WaitHelpers.Throttle(() => _initiator.Find() != null, 250), ct);

            if (_initiator.Find() != null)
            {
                BotLog.Step($"[Simulacrum] Initiator discovered at {StartPoint}.");
                return SimPhase.StartingWave;
            }
        }

        // No give-up timer: the initiator always exists — keep pathing through the
        // area until it streams in.
        await TaskUtils.NextFrame();
        return SimPhase.MovingToStart;
    }

    private async SyncTask<SimPhase> DoStartingWave(RunContext run, CancellationToken ct)
    {
        // Wave already running (adopted run, click landed, or monsters wandered in).
        var monsters = GetNearbyMonsters();
        var aff = _initiator.Find();
        SyncWaveCounter(run, aff); // the initiator's counter wins when readable

        // 15/15 complete: 'active' never goes idle again after the final wave —
        // 'goodbye' is the authoritative end-of-run signal (verified live).
        // The last wave's loot goes through the NORMAL loot pause like any other
        // wave — Finishing itself only leaves the area.
        if (CheckRunComplete(aff) is { } donePhase)
            return donePhase;

        // Loot-spawn phase (verified live): after the last kill the initiator STAYS
        // active (and untargetable) while the rewards materialize — active=1 with
        // MonstersRemaining=0 is not a running wave. Loot as it appears; the state
        // flips to idle when the phase ends.
        if (monsters.Count == 0 && _initiator.IsWaveActive(aff) == true &&
            _sawServerMonstersThisWave && _gameState.MonstersRemaining == 0)
        {
            if (_lootService.HasKnownLoot())
                return SimPhase.LootPause;

            await TaskUtils.NextFrame();
            return SimPhase.StartingWave;
        }

        // A running wave (our click landed, or an adopted run): count it and fight.
        if (_initiator.IsWaveActive(aff) == true)
        {
            // Our click started it: the initiator's number still shows the
            // PREVIOUS wave for ~1 s (verified in the logs), so count it ourselves
            // and hold that against the stale value. No click this cycle means a
            // wave that was already running (re-entry, adopted run) — then the
            // initiator's number is current and wins; +1 only when unreadable.
            // (A click from before a death/re-entry is not "ours" any more.)
            if (_waveClickAttempts > 0 && Clock.SecondsSince(_waveClickAt) < 10)
            {
                run.WavesStarted++;
                _waveCountedAt = Clock.NowMs;
            }
            else if (!_initiator.SyncWaveNumber(run, aff))
            {
                run.WavesStarted++;
            }

            _waveClickAttempts = 0;
            _lastMonsterSeenAt = Clock.NowMs;
            _sawServerMonstersThisWave = false;
            // The wave start destroyed all ground items — stale spots must not linger.
            _lootService.ForgetAll();
            _initiator.LogStates(aff, $"Wave {run.WavesStarted} started");
            return SimPhase.Fighting;
        }

        // Monsters without an active wave flag: stragglers of the previous wave, or
        // the initiator is unreadable. Fight them WITHOUT counting a wave and
        // WITHOUT forgetting loot spots — no wave start means no items were
        // destroyed. The counter syncs from the initiator once it is readable.
        if (monsters.Count > 0)
        {
            _lastMonsterSeenAt = Clock.NowMs;
            return SimPhase.Fighting;
        }

        if (run.WavesStarted >= TotalWaves)
            return SimPhase.Finishing;

        if (aff == null)
        {
            // Most likely just out of streaming range — walk back to the start.
            return SimPhase.MovingToStart;
        }

        // Ground loot is DESTROYED when the next wave starts — never click while
        // known loot exists ANYWHERE (bubble or remembered spots; screen and click
        // range are irrelevant). Throttled so unpickable leftovers can't loop us
        // between looting and clicking forever.
        if (_lootService.HasKnownLoot() &&
            Clock.SecondsSince(_lootDivertAt) > 30)
        {
            _lootDivertAt = Clock.NowMs;
            BotLog.Step("[Simulacrum] Loot on the ground -- collecting it before the next wave.");
            return SimPhase.LootPause;
        }

        // Never judge "no loot left" from afar: the wave pile at the start area
        // only enters the entity list within the network bubble, and ClickEntity
        // would cross that distance INSIDE one await — past the guard above. Get
        // near the initiator first, observing loot on the way, and re-run the
        // guards from up close. The walk only aborts on discovered loot while the
        // divert is actually available — with the divert throttled (unpickable
        // leftovers), clicking on is deliberate and the walk must finish.
        if (Vector2.Distance(_gameState.PlayerGridPos, aff.GridPosNum) > 90)
        {
            var divertAvailable = Clock.SecondsSince(_lootDivertAt) > 30;
            await _navigation.MoveToTarget(aff.GridPosNum, ct, 70,
                stopWhen: divertAvailable
                    ? WaitHelpers.Throttle(() =>
                    {
                        _lootService.ObserveGroundLoot();
                        return _lootService.HasKnownLoot();
                    }, 250)
                    : null);

            // Re-run the guards only when the walk achieved something (near now,
            // or loot discovered). A failed walk falls through to the click flow —
            // the attempt counter and its escalation own unreachability.
            if (Vector2.Distance(_gameState.PlayerGridPos, aff.GridPosNum) <= 90 ||
                _lootService.HasKnownLoot())
                return SimPhase.StartingWave;
        }

        // Quiet moment (no monsters, wave not started): catch up on gem level-ups
        // before committing the mouse to the initiator click — but never while
        // ANY loot is still known (even throttled-away leftovers): looting owns
        // the mouse until it is fully done.
        if (!_lootService.HasKnownLoot())
            await TryLevelGems(ct);

        // Throttle clicks: give a click a moment to register before the next try.
        if (_waveClickAt != Clock.Never && Clock.SecondsSince(_waveClickAt) < 1.5)
        {
            await TaskUtils.NextFrame();
            return SimPhase.StartingWave;
        }

        // Escalation exhausted: one portal re-entry resets the instance UI (the
        // usual cause is a stuck overlay/initiator state); only when that was
        // already spent — or no portals remain — is the run abandoned.
        if (_waveClickAttempts >= 6)
        {
            if (_waveStartReEntries < 1 && run.PortalsRemaining > 0)
            {
                _waveStartReEntries++;
                _waveClickAttempts = 0;
                _waveClickAt = Clock.Never;
                _log("[Simulacrum] Wave will not start -- re-entering through a portal to reset the instance.");
                run.ReEntryRequested = true;
                return SimPhase.MovingToStart;
            }

            _log($"[Simulacrum] Wave did not start after {_waveClickAttempts} clicks and a re-entry -- finishing run.");
            return SimPhase.Finishing;
        }

        // The attempt is only COUNTED once its click went out: a step cut short
        // before that (lost focus, recovery) is re-run from the top and must not
        // burn the escalation that ends in a portal re-entry.
        var attempt = _waveClickAttempts + 1;
        // The counter belongs to the PREVIOUS wave until the new one registers —
        // clearing it here keeps the loot-spawn branch from misreading the gap
        // between the click and the server's first monster count.
        _sawServerMonstersThisWave = false;

        // Escalation before the next click: attempt 3 closes whatever panel draws
        // over the initiator (the wave-reward window is the usual click
        // swallower) — but ONLY when a blocking panel is actually open; Escape
        // with nothing to close brings up the game menu, which would then swallow
        // the click itself. Attempts 4-5 re-approach from a side step so the
        // projection and highlight get a fresh angle.
        if (attempt == 3 && _gameState.HasBlockingUiPanel)
        {
            _log("[Simulacrum] Clicks are not landing -- pressing Escape to close the open panel.");
            await _input.PressKey(Keys.Escape, ct);
            await Task.Delay(300, ct);
        }
        else if (attempt is 4 or 5)
        {
            _log("[Simulacrum] Re-approaching the initiator from a different angle...");
            var side = _gameState.PlayerGridPos +
                       new Vector2(attempt == 4 ? 25 : -25, 12);
            await _navigation.MoveToTarget(side, ct, 8);
        }

        if (attempt == 1) BotLog.Step("[Simulacrum] Clicking the wave initiator...");
        else _log($"[Simulacrum] Clicking the wave initiator (attempt {attempt})...");
        await _navigation.ClickEntity(aff, ct);
        _waveClickAttempts = attempt;
        _waveClickAt = Clock.NowMs;
        return SimPhase.StartingWave;
    }

    /// <summary>
    /// Level ready gems at a quiet moment. Throttled to one attempt per 20s so a
    /// popup that refuses the click cannot stall the phase machine; skipped
    /// entirely while monsters are around.
    /// </summary>
    private async SyncTask<bool> TryLevelGems(CancellationToken ct)
    {
        if (!_settings.LevelGems) return false;
        if (Clock.SecondsSince(_gemLevelAt) < 20) return false;
        if (GetNearbyMonsters().Count > 0) return false;

        // Stamp BEFORE the panel scan: HasReadyGems walks every popup's children,
        // which must not run every frame of a quiet phase.
        _gemLevelAt = Clock.NowMs;
        if (!_gems.HasReadyGems()) return false;

        await _navigation.ReleaseMoveKey();
        await _gems.LevelReadyGems(ct);
        return true;
    }

    /// <summary>The initiator's counter wins — except during its ~1 s lag right
    /// after a wave start we counted ourselves, when it still shows the previous
    /// wave and would pull the count back by one.</summary>
    private void SyncWaveCounter(RunContext run, Entity? aff)
    {
        if (Clock.SecondsSince(_waveCountedAt) < WaveNumberLagSeconds) return;
        _initiator.SyncWaveNumber(run, aff);
    }

    private async SyncTask<SimPhase> DoFighting(RunContext run, CancellationToken ct)
    {
        // The server's remaining-monster counter covers the whole wave regardless of
        // entity streaming. Once it has been non-zero this wave (spawns take a moment,
        // so 0 right after the click means nothing), a drop back to 0 almost certainly
        // means the wave is over.
        if (_gameState.MonstersRemaining > 0)
            _sawServerMonstersThisWave = true;

        var monsters = GetNearbyMonsters();

        if (monsters.Count > 0)
        {
            _lastMonsterSeenAt = Clock.NowMs;
            // Contact = the hunt worked; disarm the frustration guard.
            _hunt.NoteContact();

            var target = _targets.Pick(monsters);
            if (target == null)
            {
                await TaskUtils.NextFrame();
                return SimPhase.Fighting;
            }

            await EngageTarget(target, ct);
            await TaskUtils.NextFrame();
            return SimPhase.Fighting;
        }

        // No targetable monsters nearby. The wave may be over, or spawns are elsewhere.
        var aff = _initiator.Find();
        var waveActive = _initiator.IsWaveActive(aff);
        SyncWaveCounter(run, aff);

        // The initiator's state flag is authoritative (verified live: active/wave = 1
        // during a wave, 0 when idle).
        if (waveActive == false)
        {
            _initiator.LogStates(aff, $"Wave {run.WavesStarted} cleared (initiator idle)");
            return SimPhase.LootPause;
        }

        // The server says nothing is left of the wave (works from anywhere) — the
        // loot-spawn phase is beginning. Head to the loot instead of patrolling on;
        // the initiator stays 'active' until the rewards finished materializing.
        // Checked BEFORE goodbye so wave 15's loot flows through the normal loot
        // pause like every other wave — StartingWave detects goodbye afterwards.
        if (waveActive != false && _sawServerMonstersThisWave && _gameState.MonstersRemaining == 0)
        {
            BotLog.Step("[Simulacrum] Server reports 0 monsters remaining -- moving to collect the wave loot.");
            _waveClickAt = Clock.Never;
            return SimPhase.LootPause;
        }

        // 15/15 complete — goodbye is the end-of-run signal. Reached here only
        // when the wave-end branches above didn't fire (e.g. adopting an already
        // completed map); route through the loot pause first like StartingWave does.
        if (CheckRunComplete(aff) is { } donePhase)
            return donePhase;

        // Only return to the initiator when the wave TRULY can't be confirmed —
        // the server counter is readable from anywhere, so a positive count is
        // confirmation enough even while hunting far away from the initiator.
        if (waveActive != true && Clock.SecondsSince(_lastMonsterSeenAt) > 10 &&
            !(_sawServerMonstersThisWave && _gameState.MonstersRemaining > 0))
        {
            _log("[Simulacrum] Can't confirm a running wave -- returning to the initiator.");
            _waveClickAt = Clock.Never;
            return SimPhase.StartingWave;
        }

        // Hunt-frustration guard: the branches below always re-pick the NEAREST
        // daemon/last-known spot, so a corner wedge re-walks the same doomed route
        // every tick forever. When the hunt has made no progress for a while,
        // relocate somewhere genuinely different first — that un-wedges the
        // character AND shifts the network bubble so other entities stream in.
        if (_gameState.MonstersRemaining > 0 && await _hunt.ShakeOffIfStuck(ct))
            return SimPhase.Fighting;

        // Prefer chasing the spawner daemons — walking near one activates its pack
        // (unspawned packs have no monster entities yet, only these daemons).
        // The walk aborts as soon as monsters appear.
        var daemon = _hunt.FindNearestSpawnerDaemon();
        if (daemon != null)
        {
            var reached = await _navigation.MoveToTarget(daemon.GridPosNum, ct, 20,
                stopWhen: StopWhenMonstersNearby());
            if (!reached && daemon.IsValid && GetNearbyMonsters().Count == 0)
                _hunt.ReportDaemonUnreachable(daemon);
            return SimPhase.Fighting;
        }

        // Monsters that streamed OUT of the network bubble (the valid entity list
        // only covers ~250 units) linger in the FULL entity list as
        // invalid-but-alive, frozen at their last-known position — walk there
        // directly. Once close, they stream in and normal targeting takes over.
        if (_gameState.MonstersRemaining > 0 &&
            _hunt.FindNearestOutOfBubbleMonster() is { } lastKnown)
        {
            BotLog.Step($"[Simulacrum] Hunting a remaining monster last seen near {lastKnown}...");
            await _navigation.MoveToTarget(lastKnown, ct, 30,
                stopWhen: StopWhenMonstersNearby());
            return SimPhase.Fighting;
        }

        // Nothing known anywhere (packs that never spawned client-side): lap the
        // exploration tour until something streams in.
        if (_gameState.MonstersRemaining > 0)
        {
            await _exploration.WalkTourLeg(StopWhenMonstersNearby(), ct);
            return SimPhase.Fighting;
        }

        // Nothing to chase: hold at the start area.
        var start = aff?.GridPosNum ?? StartPoint;
        if (start != null && Vector2.Distance(_gameState.PlayerGridPos, start.Value) > 30)
            await _navigation.MoveToTarget(start.Value, ct, 20,
                stopWhen: StopWhenMonstersNearby());
        else
            await TaskUtils.NextFrame();
        return SimPhase.Fighting;
    }

    /// <summary>
    /// The wave loot skeleton: pickup passes with deposit-and-reloot when an item
    /// does not fit, PLUS a guaranteed visit to the loot point before concluding —
    /// mid-wave drops can pull the pickup far away, and the wave
    /// rewards spawn at the loot point regardless; "nothing known anymore" from
    /// across the map proves nothing. After the visit, the reward-spawn wait runs,
    /// then a short linger for late-spawning items. Returns the last pass result.
    /// </summary>
    private async SyncTask<LootResult> RunLootPasses(
        RunContext run, Vector2? lootPoint, Func<bool>? abortWhen, CancellationToken ct)
    {
        var visitedLootPoint = false;
        // Anchored to the PAUSE START: the wave rewards materialize within a few
        // seconds of the wave ending, so time already spent picking counts against
        // this window — a pickup that took longer than the window means the
        // rewards are long since spawned and there is nothing left to wait for.
        var spawnDeadline = Clock.InSeconds(_settings.LootSpawnWaitSeconds);
        var result = LootResult.AllPicked;
        for (var pass = 0; pass < 6 && !ct.IsCancellationRequested; pass++)
        {
            result = await _lootService.PickupWindow(abortWhen, ct);

            if (result == LootResult.InventoryFull)
            {
                // The trip budget is spent and the in-area stash is unusable:
                // re-running the deposit would be the same failing lap again.
                // Leave the rest on the ground and let the wave go on.
                if (_inAreaStashGivenUp)
                {
                    _log("[Simulacrum] Inventory is full and stashing is off for this run -- " +
                         "leaving the remaining loot on the ground.");
                    return LootResult.Incomplete;
                }

                _log("[Simulacrum] An item did not fit -- depositing, then re-looting.");
                var stashResult = await StashInArea(ct);
                if (stashResult == StashResult.Ok)
                    continue;

                // Wrong dump-tab settings fail the hideout stash exactly the same
                // way — a trip would only burn portals.
                if (stashResult == StashResult.ConfigError)
                {
                    _inAreaStashGivenUp = true;
                    _log("[Simulacrum] Stashing is blocked by the dump-tab settings -- no hideout trip " +
                         "can fix that; leaving the remaining loot on the ground for this run.");
                    return LootResult.Incomplete;
                }

                // In-area stashing failed after everything StashService can try.
                // The escalation is NOT another lap of clicking: go out through
                // the portal, empty the inventory in the hideout, and come back
                // in through a remaining portal. The loot stays on the ground,
                // and this pause resumes right here after the re-entry.
                if (_stashTripsTaken < MaxStashTrips && run.PortalsRemaining >= MinPortalsForStashTrip)
                {
                    _stashTripsTaken++;
                    _stashTripRequested = true;
                    _log($"[Simulacrum] In-area stashing failed -- emptying the inventory in the " +
                         $"hideout and coming back (trip {_stashTripsTaken}/{MaxStashTrips}, " +
                         $"{run.PortalsRemaining} portals left).");
                    return LootResult.Incomplete;
                }

                _inAreaStashGivenUp = true;
                _log(_stashTripsTaken >= MaxStashTrips
                    ? "[Simulacrum] In-area stashing failed and both hideout trips are spent -- " +
                      "the waves go on, but loot that does not fit stays on the ground for this run."
                    : $"[Simulacrum] In-area stashing failed and only {run.PortalsRemaining} portal(s) " +
                      "are left (a trip needs 2) -- the waves go on, but loot that does not fit " +
                      "stays on the ground for this run.");
                return LootResult.Incomplete;
            }

            if (result != LootResult.AllPicked)
            {
                if (result == LootResult.Incomplete)
                    _log("[Simulacrum] Loot pass ended with items left on the ground.");
                return result;
            }

            // Everything KNOWN is picked — now be near the loot point once (close
            // enough for its entities and labels to stream in; no need to stand on
            // it) and give the wave rewards time to materialize. Loot discovered
            // en route or during the wait goes through another pickup pass.
            if (!visitedLootPoint && lootPoint != null)
            {
                if (Vector2.Distance(_gameState.PlayerGridPos, lootPoint.Value) > 90)
                {
                    BotLog.Step("[Simulacrum] Checking the wave loot point before finishing the pause...");
                    await _navigation.MoveToTarget(lootPoint.Value, ct, 60,
                        stopWhen: WaitHelpers.Throttle(() =>
                        {
                            _lootService.ObserveGroundLoot();
                            return _lootService.HasKnownLoot();
                        }, 250));
                }

                if (Vector2.Distance(_gameState.PlayerGridPos, lootPoint.Value) <= 90)
                {
                    visitedLootPoint = true;

                    while (!Clock.Reached(spawnDeadline) && !ct.IsCancellationRequested &&
                           !_lootService.HasKnownLoot() && GetNearbyMonsters().Count == 0)
                    {
                        await Task.Delay(200, ct);
                    }
                }
                else if (!_lootService.HasKnownLoot())
                {
                    // Walk failed outright (no route) with nothing new discovered —
                    // don't loop the pause on an unreachable point.
                    visitedLootPoint = true;
                }

                if (abortWhen?.Invoke() == true)
                    return LootResult.Aborted;

                if (_lootService.HasKnownLoot())
                    continue;
            }

            // Everything picked and the loot point checked — linger briefly: the
            // game sometimes spawns a single reward item well after the rest.
            var lingerUntil = Clock.InSeconds(_settings.LateLootLingerSeconds);
            var lateLoot = false;
            while (!Clock.Reached(lingerUntil) && !ct.IsCancellationRequested)
            {
                if (abortWhen?.Invoke() == true)
                    return LootResult.Aborted;

                _lootService.ObserveGroundLoot();
                if (_lootService.HasKnownLoot())
                {
                    lateLoot = true;
                    break;
                }

                await Task.Delay(200, ct);
            }

            if (!lateLoot)
                return result;

            BotLog.Step("[Simulacrum] Late loot spawned -- picking it up.");
        }

        return result;
    }

    private async SyncTask<SimPhase> DoLootPause(RunContext run, CancellationToken ct)
    {
        await _navigation.ReleaseMoveKey();

        // Rewards drop around the start location — being near the initiator is enough
        // for the labels to be detected.
        var lootPoint = _initiator.Find()?.GridPosNum ?? StartPoint;

        var result = await RunLootPasses(run, lootPoint,
            abortWhen: StopWhenMonstersNearby(), ct);

        // Several of the pass loops end quietly on cancellation and hand back
        // their last result — never act on a cut-short pause (gem clicks, the
        // next wave) as if it had finished.
        ct.ThrowIfCancellationRequested();

        // A hideout stash trip is owed: the engine owns leaving and re-entering
        // (ReEntryRequested), so hand it over and come back INTO the loot pause —
        // the loot is still lying in the instance, waiting for a free inventory.
        if (_stashTripRequested)
        {
            _stashTripRequested = false;
            run.ReEntryRequested = true;
            run.StashTripRequested = true;
            return SimPhase.LootPause;
        }

        // Monsters during looting are stragglers from the SAME wave (waves never
        // start on their own) — go fight them without counting a new wave.
        if (result == LootResult.Aborted || GetNearbyMonsters().Count > 0)
        {
            BotLog.Step("[Simulacrum] Monsters interrupted looting -- resuming the fight.");
            return SimPhase.Fighting;
        }

        // Post-wave chores run ONLY once looting fully succeeded (an Incomplete
        // pass means items are still on the ground — moving the mouse to popups
        // now would interleave with further loot handling).
        if (result == LootResult.AllPicked)
            await TryLevelGems(ct);

        _waveClickAt = Clock.Never;
        return SimPhase.StartingWave;
    }

    private async SyncTask<SimPhase> DoFinishing(RunContext run, CancellationToken ct)
    {
        // The last wave's loot was collected by the normal loot pause (goodbye
        // routes through it) — finishing only means finding the way out.
        await _navigation.ReleaseMoveKey();
        _log("[Simulacrum] Run complete -- leaving the area.");

        // The way back must be DISCOVERED, never assumed: if no portal was seen this
        // run (e.g. the bot was started mid-run), walk the exploration tour until one
        // streams into the bubble. Deadline-bounded: if none shows up, conclude
        // anyway — the engine's ReturningToHideout has its own (webhook-alerting)
        // portal search and owns the truly-stuck case.
        if (run.MapEntryPoint == null && _gameState.GetTownPortals().Count == 0)
        {
            _log("[Simulacrum] No portal discovered this run -- searching the area for it.");

            // Rule out a stale host (missed area change) before walking a tour.
            if (_gameState.TryForceRefreshArea())
                await Task.Delay(1000, ct);

            var deadline = Clock.InMinutes(3);
            await _exploration.ExploreUntil(
                WaitHelpers.Throttle(
                    () => _gameState.GetTownPortals().Count > 0 || Clock.Reached(deadline), 250), ct);

            var portal = _gameState.GetTownPortals()
                .OrderBy(p => Vector2.Distance(_gameState.PlayerGridPos, p.GridPosNum))
                .FirstOrDefault();
            if (portal != null)
            {
                run.MapEntryPoint = portal.GridPosNum;
                _log($"[Simulacrum] Portal discovered at {portal.GridPosNum}.");
            }
            else
            {
                _log("[Simulacrum] No portal found within 3 minutes -- handing the search to the engine.");
            }
        }

        return SimPhase.Completed;
    }

    /// <summary>
    /// Walk to the in-area stash (via the position remembered this instance while
    /// the entity is not yet streamed in; exploring for it if never seen) and
    /// deposit. The exploration is deadline-bounded so a stash that never streams
    /// in cannot loop the pause forever — the deposit attempt below then fails
    /// gracefully and the loot pass carries on with what fits.
    /// </summary>
    private async SyncTask<StashResult> StashInArea(CancellationToken ct)
    {
        if (_gameState.FindStash() == null)
        {
            if (StashPoint is { } stashPoint)
            {
                await _navigation.MoveToTarget(stashPoint, ct, 25);
            }
            else
            {
                // Stash never seen this instance — explore until it streams in.
                // Throttled: FindStash falls back to a full-list path scan while
                // it is missing, which is the common case during the search.
                _log("[Simulacrum] Looking for the in-area stash...");
                var deadline = Clock.InMinutes(3);
                await _exploration.ExploreUntil(
                    WaitHelpers.Throttle(
                        () => _gameState.FindStash() != null || Clock.Reached(deadline), 250), ct);

                if (_gameState.FindStash() is { } found)
                    _areas.SetPoint(_gameState.AreaHash, AreaMemory.SimStashKey, found.GridPosNum);
                else
                    _log("[Simulacrum] No stash found within 3 minutes -- attempting the deposit anyway.");
            }
        }

        var result = await _stashService.Execute(ct);

        if (result != StashResult.Ok && !_warnedInventoryFull)
        {
            _warnedInventoryFull = true;
            _log($"[Simulacrum] In-area stashing failed -- {_stashService.LastFailure ?? "unknown step"}.");
        }
        return result;
    }

    /// <summary>
    /// Move within hover range of the target, then park the mouse on its
    /// Render.InteractCenter so ReAgent can engage. Uniques use their own
    /// preferred distance setting. (Reachability is guarded by the stall bench
    /// in TargetSelector, which covers approach and hover alike.)
    /// </summary>
    private async SyncTask<bool> EngageTarget(Entity target, CancellationToken ct)
    {
        float hoverRange = target.Rarity == MonsterRarity.Unique
            ? _settings.UniqueHoverRange
            : _settings.HoverRange;

        // Temporal-bubble rares (verified live: ObjectMagicProperties.Mods contains
        // "MonsterModTemporalAura") are engaged from inside the bubble — hovering
        // from normal range leaves ReAgent attacking a target our hits can't
        // meaningfully reach.
        if (_settings.MoveCloseToProximityShieldRares &&
            target.Rarity == MonsterRarity.Rare && HasTemporalAura(target))
        {
            hoverRange = 15f;
        }

        // Hover only when the target is close AND actually in line of sight — the
        // terrain targeting grid tells us directly whether a wall is between us
        // (cheap Bresenham, checked every tick). Sight blocked or too far → grid-path
        // walk that FOLLOWS the monster as it moves (live-target route) and ends
        // early when: both conditions become true mid-route, the target dies, or a
        // STRICTLY better target appears (unique spawns next to us while we cross
        // the map for a rare). When sight is blocked, arrival is judged by
        // remaining path so the walk can't "arrive" at the wall.
        var dist = Vector2.Distance(_gameState.PlayerGridPos, target.GridPosNum);
        var inSight = _gameState.HasLineOfSight(_gameState.PlayerGridPos, target.GridPosNum);

        if (dist > hoverRange || !inSight)
        {
            // While an ability animation plays, STARTING a walk would cancel it —
            // skip movement this tick and re-evaluate next frame (walks already
            // running are untouched; this only gates the start). Targets we must
            // reposition for override: ACD totems, temporal-bubble rares, uniques.
            if (_settings.NoMovementDuringAbilities &&
                _gameState.IsPlayerAttacking &&
                TargetSelector.TargetScore(target) < 100 &&
                target.Rarity != MonsterRarity.Unique &&
                !(target.Rarity == MonsterRarity.Rare && HasTemporalAura(target)))
            {
                await TaskUtils.NextFrame();
                return true;
            }

            var currentScore = TargetSelector.TargetScore(target);
            var lastRecheck = Clock.Never;

            // Freeze the last valid position so the route has a stable destination
            // during the brief window between the target despawning and stopWhen
            // cancelling the walk.
            var lastKnownPos = target.GridPosNum;
            Vector2 LiveTargetPos()
            {
                if (target.IsValid) lastKnownPos = target.GridPosNum;
                return lastKnownPos;
            }

            // Sight-blocked walks use a SMALL path-based stop distance: the walk
            // exists to get AROUND the wall, and only stopWhen (range + sight)
            // may end it early. With hoverRange*0.8 as a PATH budget, a monster
            // just past the end of a wall counts as "arrived" while we stand at
            // the wall — the walk ends instantly every tick without moving.
            var stopDistance = inSight ? hoverRange * 0.8f : 6f;
            var posBefore = _gameState.PlayerGridPos;

            await _navigation.MoveToTarget(LiveTargetPos, ct, stopDistance,
                pathArrival: !inSight, keepMoveKeyHeld: true,
                stopWhen: () =>
                {
                    if (!target.IsValid || !target.IsAlive)
                        return true;

                    if (Vector2.Distance(_gameState.PlayerGridPos, target.GridPosNum) <= hoverRange &&
                        _gameState.HasLineOfSight(_gameState.PlayerGridPos, target.GridPosNum))
                        return true;

                    // Throttled better-target check. Aborts the walk when a strictly
                    // higher tier appeared, or when we are trekking to something
                    // OUTSIDE the near band while a target INSIDE it exists
                    // (near-first: never cross the map through packs).
                    if (Clock.MsSince(lastRecheck) > 250)
                    {
                        lastRecheck = Clock.NowMs;
                        // Peek, never Pick: picking here would run the target hold
                        // out mid-walk and swap targets behind the walk's back.
                        var best = _targets.Peek(GetNearbyMonsters());
                        if (best != null && best.Id != target.Id)
                        {
                            if (TargetSelector.TargetScore(best) > currentScore)
                                return true;

                            float nearRadius = _settings.NearTargetRadius;
                            var playerPos = _gameState.PlayerGridPos;
                            if (Vector2.Distance(playerPos, best.GridPosNum) <= nearRadius &&
                                Vector2.Distance(playerPos, target.GridPosNum) > nearRadius)
                                return true;
                        }
                    }

                    return false;
                });

            // Stall guard: the walk ended, yet we neither moved nor gained
            // range+sight on a still-valid target (dead-end wall, unwalkable
            // pocket, no route). The selector benches repeat offenders so
            // targeting moves on to something reachable.
            var stalled = target.IsValid && target.IsAlive &&
                          Vector2.Distance(_gameState.PlayerGridPos, posBefore) < 3 &&
                          (Vector2.Distance(_gameState.PlayerGridPos, target.GridPosNum) > hoverRange ||
                           !_gameState.HasLineOfSight(_gameState.PlayerGridPos, target.GridPosNum));
            if (stalled)
                _targets.ReportEngageStall(target.Id);
            else
                _targets.ClearEngageStalls(target.Id);

            return true;
        }

        await _navigation.ReleaseMoveKey();
        var center = target.GetComponent<Render>()?.InteractCenterNum ?? target.PosNum;
        await _navigation.MoveMouseToScreen(_gameState.WorldToScreen(center), ct);
        return true;
    }

    private IReadOnlyList<Entity>? _monstersCache;
    private long _monstersCachedAt = Clock.Never;

    /// <summary>
    /// Viable wave monsters: alive + hostile + targetable (service-level), minus the
    /// configured ignore list — Delirium volatiles must never be hovered or counted.
    /// Cached for 100ms: several callers per tick (phases, stopWhen lambdas, loot
    /// aborts) would otherwise re-scan the entity list with component reads.
    /// </summary>
    private IReadOnlyList<Entity> GetNearbyMonsters()
    {
        if (_monstersCache != null && Clock.MsSince(_monstersCachedAt) < 100)
            return _monstersCache;

        _monstersCachedAt = Clock.NowMs;
        // No radius: the server's network bubble already bounds the entity list.
        return _monstersCache = _gameState.FindNearbyMonsters()
            // Never treat the wave-initiator NPC as a monster, no matter how the
            // game happens to classify it in the entity list.
            .Where(m => m.Path == null || !m.Path.Contains(AfflictionatorGateway.PathFragment))
            .Where(m => m.Path == null || !IgnoredMonsterPaths.Any(m.Path.Contains))
            .ToList();
    }

    private readonly Dictionary<uint, bool> _temporalAuraCache = [];

    /// <summary>
    /// Does this monster carry the Temporal Bubble aura mod? Read from
    /// ObjectMagicProperties.Mods (verified live: "MonsterModTemporalAura").
    /// Cached per entity id — mods never change during a fight, and this runs
    /// every engage tick. Unreadable component (not streamed yet) is NOT cached
    /// so it is re-read once available.
    /// </summary>
    private bool HasTemporalAura(Entity target)
    {
        if (_temporalAuraCache.TryGetValue(target.Id, out var cached))
            return cached;

        return SafeRead.Try("sim.temporal-aura", () =>
        {
            var mods = target.GetComponent<ObjectMagicProperties>()?.Mods;
            if (mods == null) return false;

            var has = mods.Any(m => m != null && m.Contains("MonsterModTemporalAura"));
            _temporalAuraCache[target.Id] = has;
            return has;
        });
    }

    /// <summary>
    /// Walk-abort condition for daemon-chasing and hold-walk legs: monsters showed
    /// up. Throttled — the entity scan is not free.
    /// </summary>
    private Func<bool> StopWhenMonstersNearby() =>
        WaitHelpers.Throttle(() => GetNearbyMonsters().Count > 0, 250);

    private long _portalCheckedAt = Clock.Never;

    /// <summary>
    /// Keep the return-portal position current: portals move when the player drops a
    /// new one (portal scroll), so whenever any town portal is in view, remember the
    /// nearest one as the way back. Throttled; run-scoped (the per-layout entry point
    /// in the store stays the map-device entrance).
    /// </summary>
    private void UpdatePortalKnowledge(RunContext run)
    {
        if (Clock.SecondsSince(_portalCheckedAt) < 2) return;
        _portalCheckedAt = Clock.NowMs;

        var playerPos = _gameState.PlayerGridPos;
        var portal = _gameState.GetTownPortals()
            .OrderBy(p => Vector2.Distance(playerPos, p.GridPosNum))
            .FirstOrDefault();
        if (portal != null)
        {
            run.MapEntryPoint = portal.GridPosNum;
            PortalPoint = portal.GridPosNum; // survives bot restarts in this instance
        }
        else if (run.MapEntryPoint == null && PortalPoint != null)
        {
            // Bot restarted mid-run: the run context is fresh but the portal was
            // already discovered in this map instance.
            run.MapEntryPoint = PortalPoint;
        }
    }

    private enum SimPhase
    {
        MovingToStart,
        StartingWave,
        Fighting,
        LootPause,
        Finishing,
        Completed
    }
}
