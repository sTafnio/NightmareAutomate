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
/// No recorded locations: the only discovered things are the wave initiator, the
/// in-area stash, and the return portal — found by exploring until they stream
/// into entity range and cached for the current MAP INSTANCE only (area hash),
/// never across runs or to disk.
/// </summary>
public sealed class SimulacrumStrategy : IFarmStrategy
{
    public const string FragmentPath = "Metadata/Items/MapFragments/CurrencyAfflictionFragment";

    /// <summary>Wave initiator entity. Both the containing folder AND the act suffix
    /// differ per layout (e.g. .../TownArena/Objects/AfflictionatorAct6 in Lunacy's
    /// Watch, .../Act8_DeliriumTown/Objects/AfflictionatorAct8 in The Syndrome
    /// Encampment) — only "/Objects/Afflictionator" is common to all. Must not match
    /// the AfflictionatorPackSpawnerDaemon monsters.</summary>
    private const string AfflictionatorPathFragment = "/Objects/Afflictionator";

    /// <summary>Invisible spawn-point daemons scattered across the area during a wave.
    /// Walking near one activates its pack — chasing these replaces blind patrolling.</summary>
    private const string SpawnerDaemonPathFragment = "LeagueAffliction/AfflictionatorPackSpawnerDaemon";

    private const int TotalWaves = 15;

    /// <summary>Targeted before EVERYTHING, even uniques — these totems make the
    /// monsters around them invulnerable, so nothing dies until they do.</summary>
    private static readonly string[] PriorityMonsterPaths = ["TotemAlliesCannotDie"];

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
    private readonly NightmareAutomateSettings _settings;
    private readonly Action<string> _log;

    /// <summary>
    /// The three discovered positions of the CURRENT map instance: wave initiator,
    /// in-area stash, return portal. Entities stream out with distance, so the last
    /// seen position is kept to navigate back. Keyed by the area INSTANCE hash —
    /// static so a bot restart inside the same map keeps them; a new Simulacrum
    /// instance invalidates them automatically. Never persisted to disk.
    /// </summary>
    private static uint _pointsAreaHash;
    private static Vector2? _startPointCache;
    private static Vector2? _stashPointCache;
    private static Vector2? _portalPointCache;

    private void EnsurePointCacheIsForCurrentInstance()
    {
        // Only a DIFFERENT Simulacrum instance invalidates — trips to the
        // hideout/town mid-run must not wipe the points.
        if (!IsFarmArea(_gameState.AreaName)) return;
        var hash = _gameState.AreaHash;
        if (hash == 0 || hash == _pointsAreaHash) return;
        _pointsAreaHash = hash;
        _startPointCache = null;
        _stashPointCache = null;
        _portalPointCache = null;
    }

    private Vector2? StartPoint
    {
        get { EnsurePointCacheIsForCurrentInstance(); return _startPointCache; }
        set { EnsurePointCacheIsForCurrentInstance(); _startPointCache = value; }
    }

    private Vector2? StashPoint
    {
        get { EnsurePointCacheIsForCurrentInstance(); return _stashPointCache; }
        set { EnsurePointCacheIsForCurrentInstance(); _stashPointCache = value; }
    }

    private Vector2? PortalPoint
    {
        get { EnsurePointCacheIsForCurrentInstance(); return _portalPointCache; }
        set { EnsurePointCacheIsForCurrentInstance(); _portalPointCache = value; }
    }

    private SimPhase _phase = SimPhase.MovingToStart;
    private DateTime _lastMonsterSeenAt = DateTime.MinValue;
    private DateTime _waveClickAt = DateTime.MinValue;
    private int _waveClickAttempts;
    private int _waveStartReEntries;
    private DateTime _gemLevelAt = DateTime.MinValue;
    private bool _warnedInventoryFull;
    private bool _settleDone;
    private bool _sawServerMonstersThisWave;
    private DateTime _lootDivertAt = DateTime.MinValue;
    private readonly Dictionary<uint, DateTime> _daemonSkipUntil = [];
    private readonly Dictionary<uint, int> _engageStalls = [];
    private readonly Dictionary<uint, DateTime> _targetSkipUntil = [];

    public SimulacrumStrategy(
        NavigationService navigation,
        GameStateService gameState,
        LootService lootService,
        InventoryService inventory,
        StashService stashService,
        ExplorationService exploration,
        InputService input,
        GemLevelService gems,
        NightmareAutomateSettings settings,
        Action<string> log)
    {
        _navigation = navigation;
        _gameState = gameState;
        _lootService = lootService;
        _inventory = inventory;
        _stashService = stashService;
        _exploration = exploration;
        _input = input;
        _gems = gems;
        _settings = settings;
        _log = log;
    }

    public string Name => "Simulacrum";
    public string CurrentPhaseName => _phase.ToString();

    public IReadOnlyList<string> MapDeviceItemPaths { get; } = new[] { FragmentPath };

    public bool IsFarmArea(string areaName) => AreaNameSet.Contains(areaName);

    public IEnumerable<(string Label, string Value)> GetStatusRows(RunContext run)
    {
        yield return ("Waves", $"{run.WavesStarted}/{TotalWaves}");
    }

    public void Reset()
    {
        _phase = SimPhase.MovingToStart;
        _lastMonsterSeenAt = DateTime.MinValue;
        _waveClickAt = DateTime.MinValue;
        _waveClickAttempts = 0;
        _waveStartReEntries = 0;
        _gemLevelAt = DateTime.MinValue;
        _warnedInventoryFull = false;
        _settleDone = false;
        _sawServerMonstersThisWave = false;
        _lootDivertAt = DateTime.MinValue;
        _monstersCache = null;
        _monstersCachedAt = DateTime.MinValue;
        _lootService.ForgetAll();
        _daemonSkipUntil.Clear();
        _engageStalls.Clear();
        _targetSkipUntil.Clear();
        _temporalAuraCache.Clear();
    }

    public async SyncTask<bool> ExecuteFarm(RunContext run, CancellationToken ct)
    {
        UpdatePortalKnowledge(run);

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
    /// The wave initiator, when streamed into memory. Entities load/unload with
    /// distance — a null here often just means "too far away", which is why
    /// navigation falls back to the position remembered for this run.
    /// Remembers its position (and the stash's) for the CURRENT run when seen.
    /// </summary>
    private Entity? FindAfflictionator()
    {
        var aff = _gameState.FindEntitiesByPath(AfflictionatorPathFragment)
            .FirstOrDefault(e => e.IsValid);

        if (aff != null)
            StartPoint = aff.GridPosNum;

        var stash = _gameState.FindStash();
        if (stash != null)
            StashPoint = stash.GridPosNum;

        return aff;
    }

    /// <summary>
    /// Read the wave state from the Afflictionator's StateMachine. Verified live:
    /// 'active' is the wave-running flag (1 during, 0 idle) BUT stays 1 forever once
    /// the run is complete; 'wave' is the wave counter and PERSISTS between waves
    /// (idle after wave 1 reads active:0/wave:1), so only 'active' may decide whether
    /// a wave is running; 'goodbye' flips to 1 exactly at 15/15 completion — the
    /// authoritative end-of-run signal.
    /// Nulls when the state can't be read (entity unloaded / component missing).
    /// </summary>
    private (bool? Active, int? WaveNumber, bool? Goodbye) GetWaveState(Entity? afflictionator)
    {
        try
        {
            var states = afflictionator?.GetComponent<StateMachine>()?.States;
            if (states == null) return (null, null, null);

            long active = 0, wave = 0, goodbye = 0;
            var found = false;
            foreach (var s in states)
            {
                if (s.Name == "active") { active = s.Value; found = true; }
                else if (s.Name == "wave") { wave = s.Value; }
                else if (s.Name == "goodbye") { goodbye = s.Value; }
            }

            if (!found) return (null, null, null);
            return (active != 0, wave > 0 ? (int)wave : null, goodbye != 0);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private bool? IsWaveActive(Entity? afflictionator) => GetWaveState(afflictionator).Active;

    private bool? IsRunComplete(Entity? afflictionator) => GetWaveState(afflictionator).Goodbye;

    /// <summary>
    /// Shared 15/15 handling ('goodbye' is the authoritative end-of-run signal):
    /// route through the loot pause first (throttled, so unpickable leftovers
    /// can't loop us), then finish. Null while the run is not complete.
    /// </summary>
    private SimPhase? CheckRunComplete(Entity? afflictionator)
    {
        if (IsRunComplete(afflictionator) != true) return null;

        if (_lootService.HasKnownLoot() &&
            (DateTime.Now - _lootDivertAt).TotalSeconds > 30)
        {
            _lootDivertAt = DateTime.Now;
            _log("[Simulacrum] Run complete -- collecting the remaining loot first.");
            return SimPhase.LootPause;
        }

        _log("[Simulacrum] Initiator says goodbye -- run complete.");
        return SimPhase.Finishing;
    }

    /// <summary>
    /// The initiator's wave counter is the truth — e.g. when the bot is started at
    /// wave 5, our own count would otherwise begin at 1.
    /// </summary>
    private void SyncWaveNumber(RunContext run, Entity? afflictionator)
    {
        var (_, waveNumber, _) = GetWaveState(afflictionator);
        if (waveNumber.HasValue && waveNumber.Value != run.WavesStarted)
        {
            _log($"[Simulacrum] Wave counter synced from initiator: {run.WavesStarted} -> {waveNumber.Value}.");
            run.WavesStarted = waveNumber.Value;
        }
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

        var area = _gameState.AreaName;

        // Resuming mid-wave (bot restarted inside the map): monsters are already
        // around us — adopt the running wave right here instead of walking away.
        if (GetNearbyMonsters().Count > 0)
        {
            _log("[Simulacrum] Monsters around us -- resuming the running wave.");
            return SimPhase.StartingWave;
        }

        // The initiator only needs to be STREAMED IN to read its state (verified
        // readable at 160+ units) — never walk on top of it just to look at it.
        var aff = FindAfflictionator();
        if (aff != null)
            return SimPhase.StartingWave;

        // Not loaded. If the initiator was already discovered in this map instance,
        // walk back toward its remembered position until it streams in.
        if (StartPoint is { } startPoint)
        {
            _log($"[Simulacrum] Moving toward the start point {startPoint}...");
            await _navigation.MoveToTarget(startPoint, ct, 90);
            aff = FindAfflictionator();

            if (aff == null &&
                Vector2.Distance(_gameState.PlayerGridPos, startPoint) < 100)
            {
                // Close and still nothing — step in further before giving up.
                await _navigation.MoveToTarget(startPoint, ct, 20);
                aff = FindAfflictionator();
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
                WaitHelpers.Throttle(() => _gameState.FindEntitiesByPath(AfflictionatorPathFragment)
                    .Any(e => e.IsValid), 250), ct);

            if (FindAfflictionator() != null)
            {
                _log($"[Simulacrum] Initiator discovered at {StartPoint}.");
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
        var aff = FindAfflictionator();
        SyncWaveNumber(run, aff); // the initiator's counter wins when readable

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
        if (monsters.Count == 0 && IsWaveActive(aff) == true &&
            _sawServerMonstersThisWave && _gameState.MonstersRemaining == 0)
        {
            if (_lootService.HasKnownLoot())
                return SimPhase.LootPause;

            await TaskUtils.NextFrame();
            return SimPhase.StartingWave;
        }

        // A running wave (our click landed, or an adopted run): count it and fight.
        if (IsWaveActive(aff) == true)
        {
            run.WavesStarted++;
            SyncWaveNumber(run, aff);
            _waveClickAttempts = 0;
            _lastMonsterSeenAt = DateTime.Now;
            _sawServerMonstersThisWave = false;
            // The wave start destroyed all ground items — stale spots must not linger.
            _lootService.ForgetAll();
            LogAfflictionatorStates(aff, $"wave {run.WavesStarted} started");
            return SimPhase.Fighting;
        }

        // Monsters without an active wave flag: stragglers of the previous wave, or
        // the initiator is unreadable. Fight them WITHOUT counting a wave and
        // WITHOUT forgetting loot spots — no wave start means no items were
        // destroyed. The counter syncs from the initiator once it is readable.
        if (monsters.Count > 0)
        {
            _lastMonsterSeenAt = DateTime.Now;
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
            (DateTime.Now - _lootDivertAt).TotalSeconds > 30)
        {
            _lootDivertAt = DateTime.Now;
            _log("[Simulacrum] Loot on the ground -- collecting it before the next wave.");
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
            var divertAvailable = (DateTime.Now - _lootDivertAt).TotalSeconds > 30;
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
        if (_waveClickAt != DateTime.MinValue && (DateTime.Now - _waveClickAt).TotalSeconds < 1.5)
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
                _waveClickAt = DateTime.MinValue;
                _log("[Simulacrum] Wave will not start -- re-entering through a portal to reset the instance.");
                run.ReEntryRequested = true;
                return SimPhase.MovingToStart;
            }

            _log($"[Simulacrum] Wave did not start after {_waveClickAttempts} clicks and a re-entry -- finishing run.");
            return SimPhase.Finishing;
        }

        _waveClickAttempts++;
        _waveClickAt = DateTime.Now;
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
        if (_waveClickAttempts == 3 && _gameState.HasBlockingUiPanel)
        {
            _log("[Simulacrum] Clicks are not landing -- pressing Escape to close the open panel.");
            await _input.PressKey(Keys.Escape, ct);
            await Task.Delay(300, ct);
        }
        else if (_waveClickAttempts is 4 or 5)
        {
            _log("[Simulacrum] Re-approaching the initiator from a different angle...");
            var side = _gameState.PlayerGridPos +
                       new Vector2(_waveClickAttempts == 4 ? 25 : -25, 12);
            await _navigation.MoveToTarget(side, ct, 8);
        }

        _log($"[Simulacrum] Clicking the wave initiator (attempt {_waveClickAttempts})...");
        await _navigation.ClickEntity(aff, ct);
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
        if ((DateTime.Now - _gemLevelAt).TotalSeconds < 20) return false;
        if (GetNearbyMonsters().Count > 0) return false;

        // Stamp BEFORE the panel scan: HasReadyGems walks every popup's children,
        // which must not run every frame of a quiet phase.
        _gemLevelAt = DateTime.Now;
        if (!_gems.HasReadyGems()) return false;

        await _navigation.ReleaseMoveKey();
        await _gems.LevelReadyGems(ct);
        return true;
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
            _lastMonsterSeenAt = DateTime.Now;

            var target = PickTarget(monsters);
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
        var aff = FindAfflictionator();
        var waveActive = IsWaveActive(aff);
        SyncWaveNumber(run, aff);

        // The initiator's state flag is authoritative (verified live: active/wave = 1
        // during a wave, 0 when idle).
        if (waveActive == false)
        {
            LogAfflictionatorStates(aff, $"wave {run.WavesStarted} cleared (initiator idle)");
            return SimPhase.LootPause;
        }

        // The server says nothing is left of the wave (works from anywhere) — the
        // loot-spawn phase is beginning. Head to the loot instead of patrolling on;
        // the initiator stays 'active' until the rewards finished materializing.
        // Checked BEFORE goodbye so wave 15's loot flows through the normal loot
        // pause like every other wave — StartingWave detects goodbye afterwards.
        if (waveActive != false && _sawServerMonstersThisWave && _gameState.MonstersRemaining == 0)
        {
            _log("[Simulacrum] Server reports 0 monsters remaining -- moving to collect the wave loot.");
            _waveClickAt = DateTime.MinValue;
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
        if (waveActive != true && SecondsSince(_lastMonsterSeenAt) > 10 &&
            !(_sawServerMonstersThisWave && _gameState.MonstersRemaining > 0))
        {
            _log("[Simulacrum] Can't confirm a running wave -- returning to the initiator.");
            _waveClickAt = DateTime.MinValue;
            return SimPhase.StartingWave;
        }

        // Prefer chasing the spawner daemons — walking near one activates its pack
        // (unspawned packs have no monster entities yet, only these daemons).
        // Daemons that couldn't be reached are skipped for a while. The walk aborts
        // as soon as monsters appear.
        var daemon = FindNearestSpawnerDaemon();
        if (daemon != null)
        {
            var reached = await _navigation.MoveToTarget(daemon.GridPosNum, ct, 20,
                stopWhen: StopWhenMonstersNearby());
            if (!reached && daemon.IsValid && GetNearbyMonsters().Count == 0)
            {
                _daemonSkipUntil[daemon.Id] = DateTime.Now.AddSeconds(10);
                _log("[Simulacrum] Spawn point unreachable, skipping it for a bit.");
            }
            return SimPhase.Fighting;
        }

        // Monsters that streamed OUT of the network bubble (the valid entity list
        // only covers ~250 units) linger in the FULL entity list as
        // invalid-but-alive, frozen at their last-known position — walk there
        // directly. Once close, they stream in and normal targeting takes over.
        if (_gameState.MonstersRemaining > 0 &&
            FindNearestOutOfBubbleMonster() is { } lastKnown)
        {
            _log($"[Simulacrum] Hunting a remaining monster last seen near {lastKnown}...");
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
    /// Last-known position of the nearest alive monster OUTSIDE the network
    /// bubble: Monster-type entries in the full entity list with IsValid=false
    /// but cached IsAlive/IsHostile. The data is a frozen snapshot — a hint of
    /// where to walk, never something to fight. Positions closer than 50 units
    /// are skipped: if we stand there and the monster still isn't streamed in,
    /// the snapshot is stale (wandered off or died out of view).
    /// </summary>
    private Vector2? FindNearestOutOfBubbleMonster()
    {
        var playerPos = _gameState.PlayerGridPos;
        Vector2? best = null;
        var bestDist = float.MaxValue;

        try
        {
            foreach (var e in _gameState.Controller.EntityListWrapper.Entities)
            {
                try
                {
                    if (e == null || e.IsValid || e.Type != EntityType.Monster) continue;
                    if (!e.IsAlive || !e.IsHostile) continue;

                    var path = e.Path;
                    if (path == null || IgnoredMonsterPaths.Any(path.Contains) ||
                        path.Contains(AfflictionatorPathFragment))
                        continue;

                    var d = Vector2.Distance(playerPos, e.GridPosNum);
                    if (d > 50 && d < bestDist)
                    {
                        bestDist = d;
                        best = e.GridPosNum;
                    }
                }
                catch
                {
                    // Invalid entities carry stale addresses — skip anything unreadable.
                }
            }
        }
        catch
        {
            // The live entity list can mutate mid-enumeration — just use what we
            // got this tick; the next tick rescans.
        }

        return best;
    }

    /// <summary>
    /// Nearest alive pack-spawner daemon anywhere in entity range, skipping ones
    /// recently found unreachable.
    /// </summary>
    private Entity? FindNearestSpawnerDaemon()
    {
        var playerPos = _gameState.PlayerGridPos;
        return _gameState.FindEntitiesByPath(SpawnerDaemonPathFragment)
            .Where(e => e.IsValid && e.IsAlive &&
                        (!_daemonSkipUntil.TryGetValue(e.Id, out var until) || DateTime.Now > until))
            .OrderBy(e => Vector2.Distance(playerPos, e.GridPosNum))
            .FirstOrDefault();
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
        Vector2? lootPoint, Func<bool>? abortWhen, CancellationToken ct)
    {
        var visitedLootPoint = false;
        // Anchored to the PAUSE START: the wave rewards materialize within a few
        // seconds of the wave ending, so time already spent picking counts against
        // this window — a pickup that took longer than the window means the
        // rewards are long since spawned and there is nothing left to wait for.
        var spawnDeadline = DateTime.Now.AddSeconds(_settings.LootSpawnWaitSeconds);
        var result = LootResult.AllPicked;
        for (var pass = 0; pass < 6 && !ct.IsCancellationRequested; pass++)
        {
            result = await _lootService.PickupWindow(abortWhen, ct);

            if (result == LootResult.InventoryFull)
            {
                _log("[Simulacrum] An item did not fit -- depositing, then re-looting.");
                await StashInArea(ct);
                continue;
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
                    _log("[Simulacrum] Checking the wave loot point before finishing the pause...");
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

                    while (DateTime.Now < spawnDeadline && !ct.IsCancellationRequested &&
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
            var lingerUntil = DateTime.Now.AddSeconds(_settings.LateLootLingerSeconds);
            var lateLoot = false;
            while (DateTime.Now < lingerUntil && !ct.IsCancellationRequested)
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

            _log("[Simulacrum] Late loot spawned -- picking it up.");
        }

        return result;
    }

    private async SyncTask<SimPhase> DoLootPause(RunContext run, CancellationToken ct)
    {
        await _navigation.ReleaseMoveKey();

        // Rewards drop around the start location — being near the initiator is enough
        // for the labels to be detected.
        var lootPoint = FindAfflictionator()?.GridPosNum ?? StartPoint;

        var result = await RunLootPasses(lootPoint,
            abortWhen: StopWhenMonstersNearby(), ct);

        // Monsters during looting are stragglers from the SAME wave (waves never
        // start on their own) — go fight them without counting a new wave.
        if (result == LootResult.Aborted || GetNearbyMonsters().Count > 0)
        {
            _log("[Simulacrum] Monsters interrupted looting -- resuming the fight.");
            return SimPhase.Fighting;
        }

        // Post-wave chores run ONLY once looting fully succeeded (an Incomplete
        // pass means items are still on the ground — moving the mouse to popups
        // now would interleave with further loot handling).
        if (result == LootResult.AllPicked)
            await TryLevelGems(ct);

        _waveClickAt = DateTime.MinValue;
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
        // streams into the bubble.
        if (run.MapEntryPoint == null && _gameState.GetTownPortals().Count == 0)
        {
            _log("[Simulacrum] No portal discovered this run -- searching the area for it.");
            await _exploration.ExploreUntil(
                WaitHelpers.Throttle(() => _gameState.GetTownPortals().Count > 0, 250), ct);

            var portal = _gameState.GetTownPortals()
                .OrderBy(p => Vector2.Distance(_gameState.PlayerGridPos, p.GridPosNum))
                .FirstOrDefault();
            if (portal != null)
            {
                run.MapEntryPoint = portal.GridPosNum;
                _log($"[Simulacrum] Portal discovered at {portal.GridPosNum}.");
            }
        }

        return SimPhase.Completed;
    }

    /// <summary>
    /// Walk to the in-area stash (via the position remembered this run while the
    /// entity is not yet streamed in; exploring for it if never seen) and deposit.
    /// </summary>
    private async SyncTask<bool> StashInArea(CancellationToken ct)
    {
        if (_gameState.FindStash() == null)
        {
            if (StashPoint is { } stashPoint)
            {
                await _navigation.MoveToTarget(stashPoint, ct, 25);
            }
            else
            {
                // Stash never seen this run — explore until it streams in. Throttled:
                // FindStash falls back to a full-list path scan while it is missing,
                // which is the common case during the search.
                _log("[Simulacrum] Looking for the in-area stash...");
                await _exploration.ExploreUntil(
                    WaitHelpers.Throttle(() => _gameState.FindStash() != null, 250), ct);

                if (_gameState.FindStash() is { } found)
                    StashPoint = found.GridPosNum;
            }
        }

        var ok = await _stashService.Execute(ct);

        if (!ok && !_warnedInventoryFull)
        {
            _warnedInventoryFull = true;
            _log("[Simulacrum] In-area stashing failed -- loot may be left behind.");
        }
        return ok;
    }

    /// <summary>
    /// Move within hover range of the target, then park the mouse on its
    /// Render.InteractCenter so ReAgent can engage. Uniques use their own
    /// preferred distance setting. (Reachability is guarded by the combat-progress
    /// watchdog in the Fighting phase, which covers approach and hover alike.)
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
                TargetScore(target) < 100 &&
                target.Rarity != MonsterRarity.Unique &&
                !(target.Rarity == MonsterRarity.Rare && HasTemporalAura(target)))
            {
                await TaskUtils.NextFrame();
                return true;
            }

            var currentScore = TargetScore(target);
            var lastRecheck = DateTime.MinValue;

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
                    if ((DateTime.Now - lastRecheck).TotalMilliseconds > 250)
                    {
                        lastRecheck = DateTime.Now;
                        var best = PickTarget(GetNearbyMonsters());
                        if (best != null && best.Id != target.Id)
                        {
                            if (TargetScore(best) > currentScore)
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
            // pocket, no route). Re-walking the same target every tick would
            // freeze the bot on it — after a few stalls, bench it briefly so
            // targeting moves on to something reachable.
            var stalled = target.IsValid && target.IsAlive &&
                          Vector2.Distance(_gameState.PlayerGridPos, posBefore) < 3 &&
                          (Vector2.Distance(_gameState.PlayerGridPos, target.GridPosNum) > hoverRange ||
                           !_gameState.HasLineOfSight(_gameState.PlayerGridPos, target.GridPosNum));
            if (stalled)
            {
                var stalls = _engageStalls.GetValueOrDefault(target.Id) + 1;
                _engageStalls[target.Id] = stalls;
                if (stalls >= 3)
                {
                    _engageStalls.Remove(target.Id);
                    _targetSkipUntil[target.Id] = DateTime.Now.AddSeconds(5);
                    _log("[Simulacrum] Can't reach or sight the target -- benching it for a bit.");
                }
            }
            else
            {
                _engageStalls.Remove(target.Id);
            }

            return true;
        }

        await _navigation.ReleaseMoveKey();
        var center = target.GetComponent<Render>()?.InteractCenterNum ?? target.PosNum;
        await _navigation.MoveMouseToScreen(_gameState.WorldToScreen(center), ct);
        return true;
    }

    private IReadOnlyList<Entity>? _monstersCache;
    private DateTime _monstersCachedAt = DateTime.MinValue;

    /// <summary>
    /// Viable wave monsters: alive + hostile + targetable (service-level), minus the
    /// configured ignore list — Delirium volatiles must never be hovered or counted.
    /// Cached for 100ms: several callers per tick (phases, stopWhen lambdas, loot
    /// aborts) would otherwise re-scan the entity list with component reads.
    /// </summary>
    private IReadOnlyList<Entity> GetNearbyMonsters()
    {
        if (_monstersCache != null && (DateTime.Now - _monstersCachedAt).TotalMilliseconds < 100)
            return _monstersCache;

        _monstersCachedAt = DateTime.Now;
        // No radius: the server's network bubble already bounds the entity list.
        return _monstersCache = _gameState.FindNearbyMonsters()
            // Never treat the wave-initiator NPC as a monster, no matter how the
            // game happens to classify it in the entity list.
            .Where(m => m.Path == null || !m.Path.Contains(AfflictionatorPathFragment))
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

        try
        {
            var mods = target.GetComponent<ObjectMagicProperties>()?.Mods;
            if (mods == null) return false;

            var has = mods.Any(m => m != null && m.Contains("MonsterModTemporalAura"));
            _temporalAuraCache[target.Id] = has;
            return has;
        }
        catch
        {
            return false;
        }
    }

    private uint _currentTargetId;
    private DateTime _targetAcquiredAt = DateTime.MinValue;

    /// <summary>
    /// Target selection. NEAR-FIRST: anything within NearTargetRadius is fought
    /// before anything beyond it — the bot never runs off through packs to a
    /// distant rare. (ACD totems keep working: their aura only protects monsters
    /// near the TOTEM, so when invulnerable monsters are hitting us, the totem is
    /// inside the near band too.) Within the chosen band: ACD totems and uniques
    /// are ALWAYS on top (priority tier). Everything else blends rarity with pack
    /// size — each rarity step is worth PackSizePerTier clustered monsters, so a
    /// big enough magic pack outranks a lone rare and a big enough white pack
    /// outranks a lone magic (hovering the pack center gives ReAgent's AOE the
    /// most value). TIME-sticky: the current target is held for TargetStickSeconds
    /// unless it dies, leaves the pool, or a priority target (totem/unique)
    /// appears — prevents flip-flopping between equal picks.
    /// </summary>
    private Entity? PickTarget(IReadOnlyList<Entity> monsters)
    {
        // Targets the engage stall guard benched sit out until their skip expires.
        // If that leaves nothing, we idle quietly (ReAgent still fights whatever
        // is near) instead of re-walking into the same wall.
        if (_targetSkipUntil.Count > 0)
        {
            var now = DateTime.Now;
            monsters = monsters
                .Where(m => !_targetSkipUntil.TryGetValue(m.Id, out var until) || now > until)
                .ToList();
        }

        if (monsters.Count == 0)
        {
            _currentTargetId = 0;
            return null;
        }

        var playerPos = _gameState.PlayerGridPos;
        float nearRadius = _settings.NearTargetRadius;
        float clusterRadius = _settings.ClusterRadius;

        var near = monsters
            .Where(m => Vector2.Distance(playerPos, m.GridPosNum) <= nearRadius)
            .ToList();
        var pool = near.Count > 0 ? near : monsters;

        float Dist(Entity m) => Vector2.Distance(playerPos, m.GridPosNum);
        int Cluster(Entity m) =>
            monsters.Count(o => Vector2.Distance(o.GridPosNum, m.GridPosNum) <= clusterRadius);

        // Rarity blended with pack size: one rarity step is worth PackSizePerTier
        // clustered monsters (lone rare = 2 steps, so a magic with a pack of
        // PackSizePerTier+1 around it wins; a white pack needs 2x+1).
        var packWeight = Math.Max(1, _settings.PackSizePerTier);
        int PackScore(Entity m) => (int)m.Rarity * packWeight + Cluster(m);

        // Priority tier first (totems, then uniques — always on top), then the
        // blended pack score, distance as the final tie-break.
        var best = pool
            .OrderByDescending(TargetScore)
            .ThenByDescending(PackScore)
            .ThenBy(Dist)
            .First();

        // Time-based stickiness: hold the current target briefly so equal picks
        // can't ping-pong between frames. A current target outside the pool
        // (e.g. far away while near monsters exist) is deliberately not searched
        // for — it must lose the target. A strictly higher priority tier always
        // switches instantly.
        var current = pool.FirstOrDefault(m => m.Id == _currentTargetId);
        if (current != null && best.Id != current.Id &&
            TargetScore(best) <= TargetScore(current) &&
            (DateTime.Now - _targetAcquiredAt).TotalSeconds < _settings.TargetStickSeconds)
        {
            return current;
        }

        if (best.Id != _currentTargetId)
        {
            _currentTargetId = best.Id;
            _targetAcquiredAt = DateTime.Now;
        }

        return best;
    }

    /// <summary>
    /// Walk-abort condition for daemon-chasing and hold-walk legs: monsters showed
    /// up. Throttled — the entity scan is not free.
    /// </summary>
    private Func<bool> StopWhenMonstersNearby() =>
        WaitHelpers.Throttle(() => GetNearbyMonsters().Count > 0, 250);

    /// <summary>
    /// PRIORITY tier: ACD totems above everything, then uniques — these always
    /// outrank any pack. Everything else scores 0 here and competes via the
    /// blended pack score instead. Used both for picking and for "did something
    /// strictly better appear" checks — cluster/distance deliberately not
    /// included, so ordinary packs can't abort walks or break the target stick.
    /// </summary>
    private static int TargetScore(Entity m) =>
        m.Path != null && PriorityMonsterPaths.Any(m.Path.Contains) ? 100
        : m.Rarity == MonsterRarity.Unique ? 10
        : 0;

    private DateTime _portalCheckedAt = DateTime.MinValue;

    /// <summary>
    /// Keep the return-portal position current: portals move when the player drops a
    /// new one (portal scroll), so whenever any town portal is in view, remember the
    /// nearest one as the way back. Throttled; run-scoped (the per-layout entry point
    /// in the store stays the map-device entrance).
    /// </summary>
    private void UpdatePortalKnowledge(RunContext run)
    {
        if ((DateTime.Now - _portalCheckedAt).TotalSeconds < 2) return;
        _portalCheckedAt = DateTime.Now;

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

    private void LogAfflictionatorStates(Entity? aff, string context)
    {
        try
        {
            var states = aff?.GetComponent<StateMachine>()?.States;
            var dump = states == null
                ? "n/a"
                : string.Join(", ", states.Select(s => $"{s.Name}={s.Value}"));
            _log($"[Simulacrum] {context}. Initiator states: {dump}");
        }
        catch
        {
            _log($"[Simulacrum] {context}.");
        }
    }

    private static double SecondsSince(DateTime t) =>
        t == DateTime.MinValue ? double.MaxValue : (DateTime.Now - t).TotalSeconds;

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
