using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.Shared;
using NightmareAutomate.Enums;
using NightmareAutomate.Helpers;
using NightmareAutomate.Models;
using NightmareAutomate.Services;
using NightmareAutomate.Strategies;

namespace NightmareAutomate.Engine;

/// <summary>
/// Generic state machine that orchestrates the full farming loop.
/// Farm-agnostic -- delegates in-map logic to the active IFarmStrategy.
///
/// Yield discipline: the main loop awaits NextFrame unconditionally at the top of every
/// iteration. SyncTasks run cooperatively on the game thread -- a state handler may
/// return without awaiting, but the loop itself must never spin.
/// </summary>
public sealed class FarmingEngine
{
    /// <summary>How many engine-triggered chore attempts may fail before backing off.</summary>
    private const int MaxStashFailures = 3;

    /// <summary>Hard cap on stash attempts while the inventory is FULL — there is
    /// no backing off in that state (farming blind), so this many failures in a
    /// row stops the session loudly instead of wasting hours.</summary>
    private const int MaxUrgentStashFailures = 10;

    /// <summary>Escalating chore backoff per streak: 30s, 60s, then 120s capped.</summary>
    private static readonly int[] StashBackoffSeconds = [30, 60, 120];

    /// <summary>How often a NoFragments verdict may send the engine back into a
    /// stash-restock retry before the session is declared over for real.</summary>
    private const int MaxRestockRetries = 3;

    /// <summary>Map-device flow failures in a row that stop the session — without
    /// a working device there is nothing to farm, and endless silent retries
    /// would waste hours.</summary>
    private const int MaxMapDeviceFailures = 10;

    /// <summary>Portal entries (click + transition) failing in a row that stop the
    /// session — same failsafe shape as the map device: a portal that keeps
    /// refusing must not cycle WaitingForPortal/EnteringMap forever.</summary>
    private const int MaxPortalEntryFailures = 8;

    /// <summary>Same failsafe for the way home: the town portal out of the map,
    /// and the hideout key in town.</summary>
    private const int MaxReturnFailures = 10;

    /// <summary>Minimum age of the current area before a farm portal is clicked.
    /// The host skips its area refresh for changes under ~5 s apart; the click
    /// happens about 2 s before the map arrives, so 6 s here puts the arrival
    /// near 8 s.</summary>
    private const double MinSecondsBetweenAreaChanges = 6;

    /// <summary>Alert-only watchdogs: each sends one webhook, none acts.</summary>
    private const int UnfocusedAlertSeconds = 120;
    private const int LongLoadAlertSeconds = 180;
    private const int IdleAlertSeconds = 300;
    private const int NoProgressAlertMinutes = 20;
    private const int RepeatedDeathsPerRun = 3;

    private readonly IFarmStrategy _strategy;
    private readonly NavigationService _navigation;
    private readonly GameStateService _gameState;
    private readonly InputService _input;
    private readonly MapDeviceService _mapDevice;
    private readonly ChoresService _chores;
    private readonly EventRecovery _recovery;
    private readonly DiscordWebhookService _webhook;
    private readonly SessionStats _stats;
    private readonly ExplorationService _exploration;
    private readonly AreaMemory _areas;
    private readonly Action<string> _log;
    private readonly Func<int> _getMaxRuns;

    private bool _noReturnPortalNotified;

    private long _portalWaitStartedAt = Clock.Never;

    private int _stashFailuresInARow;
    private long _stashBackoffUntil; // 0 = no backoff (monotonic deadline)
    private int _stashBackoffLevel;
    private bool _stashTroubleNotified;
    private int _restockRetries;

    /// <summary>A death since the last hideout pass: the Omen of Amelioration may
    /// have been consumed, so a run still in progress gets ONE trip to the stash
    /// for a new omen before re-entering. Cleared by the next hideout pass.</summary>
    private bool _omenTripAfterDeath;

    /// <summary>The Stashing state was entered for the death trip — fetch the omen
    /// ONLY. Set by the hideout pass that decides on the trip and lowered by every
    /// hideout pass, so it survives an interrupted (re-run) step but can never
    /// turn the regular chores between runs into an omen-only visit.</summary>
    private bool _omenOnlyTrip;

    /// <summary>The fragment tab read empty once. A false "empty" ends a whole
    /// night of farming while a real one costs one more look, so the session
    /// only ends when a second, fresh stash session agrees.</summary>
    private bool _fragmentTabReadEmpty;

    /// <summary>Farm-portal ids standing when the current map-device preparation
    /// began (null = none in progress). Survives an interrupted step so its re-run
    /// can tell that Activate already went through; any run start clears it.</summary>
    private HashSet<uint>? _portalsBeforePreparation;

    private int _mapDeviceFailuresInARow;
    private bool _mapDeviceTroubleNotified;
    private int _portalEntryFailuresInARow;
    private bool _portalEntryTroubleNotified;
    private int _returnFailuresInARow;
    private bool _returnTroubleNotified;

    // Focus gate: no input may go out while the game window is not in front.
    private long _unfocusedSince = Clock.Never;
    private bool _unfocusedNotified;

    private long _loadingSince = Clock.Never;
    private bool _longLoadNotified;
    private long _idleSince = Clock.Never;
    private bool _idleNotified;

    // Engine-wide progress watchdog (state, phase, portals, waves, runs).
    private int _progressHash;
    private long _progressAt = Clock.NowMs;
    private bool _noProgressNotified;

    private bool _repeatedDeathsNotified;

    // Stray-panel watchdog (in-map): Escape presses are throttled, and only a
    // panel that survives several of them is worth a webhook.
    private long _strayPanelEscapeAt = Clock.Never;
    private int _strayPanelEscapes;
    private bool _strayPanelNotified;

    // Notification bookkeeping: per-run values are deltas against these
    // snapshots (taken whenever a run starts or is adopted).
    private long _sessionStartedAt = Clock.NowMs;
    private int _sessionStartDeaths;
    private long _runStartedAt = Clock.NowMs;
    private int _runStartDeaths;
    private int _runStartGems;
    private string _finishReason = "";

    public BotState CurrentState { get; private set; } = BotState.Idle;
    public IFarmStrategy Strategy => _strategy;
    public RunContext Context { get; } = new();

    /// <summary>"Stop after this run", armed from the settings UI. The live run
    /// (if any) is finished as usual — re-entries after deaths or stash trips
    /// included — and the session ends at the first point where no run is
    /// active: home in the hideout, BEFORE the hideout chores, so no fresh
    /// fragment is restocked for a run that will not happen. Per session: a new
    /// engine is built on every Start.</summary>
    public bool StopAfterRun { get; set; }

    /// <summary>Cancellation for the CURRENTLY RUNNING state-handler step —
    /// linked to the master token. Cancelled from the plugin's Tick when a
    /// death/disconnect happens mid-handler (the engine loop itself cannot see
    /// it then: it only runs between handler steps).</summary>
    private CancellationTokenSource? _stepCts;

    /// <summary>Whether an external recovery interrupt is warranted right now.
    /// Polled from the plugin's Tick — the only code that still runs while a
    /// state handler is awaiting inside this SyncTask.</summary>
    public bool NeedsRecoveryInterrupt => _recovery.ShouldRecover(CurrentState, out _);

    /// <summary>Whether the game window lost focus while a state that sends input
    /// runs. Polled from the plugin's Tick like <see cref="NeedsRecoveryInterrupt"/>:
    /// a step mid-await would otherwise keep sending keys and clicks to whatever
    /// window is in front now.</summary>
    public bool NeedsFocusInterrupt => FocusGateApplies(CurrentState) && !InputReachesGame;

    /// <summary>OS input only reaches the game while its window is in front;
    /// background input always does.</summary>
    private bool InputReachesGame => _gameState.IsWindowForeground || _input.IsBackgroundInput;

    /// <summary>States that never send game input (or, Disconnected, gate on
    /// focus themselves) are exempt from the focus pause.</summary>
    private static bool FocusGateApplies(BotState state) =>
        state is not (BotState.Idle or BotState.Disconnected or BotState.Finished or BotState.Error);

    /// <summary>
    /// Cancel the current state-handler step (NOT the whole engine): the handler
    /// unwinds, the main loop regains control, and its recovery check / focus
    /// gate takes over with the run context intact. Handlers are re-run from the
    /// top afterwards. Same-thread with the SyncTask pump, so no races. No-op
    /// when nothing runs or the step is already cancelled.
    /// </summary>
    public void InterruptStep(string reason)
    {
        var step = _stepCts;
        if (step is { IsCancellationRequested: false })
        {
            _log($"{reason} -- cancelling the current action.");
            step.Cancel();
        }
    }

    public FarmingEngine(
        IFarmStrategy strategy,
        BotServices services,
        Action<string> log,
        Func<int> getMaxRuns)
    {
        _strategy = strategy;
        _navigation = services.Navigation;
        _gameState = services.GameState;
        _input = services.Input;
        _mapDevice = services.MapDevice;
        _chores = services.Chores;
        _recovery = services.Recovery;
        _webhook = services.Webhook;
        _stats = services.Stats;
        _exploration = services.Exploration;
        _areas = services.Areas;
        _log = log;
        _getMaxRuns = getMaxRuns;
    }

    /// <summary>
    /// Main loop. Runs until cancelled or an unrecoverable error occurs.
    /// </summary>
    public async SyncTask<bool> Run(CancellationToken ct)
    {
        try
        {
            _sessionStartedAt = Clock.NowMs;
            _sessionStartDeaths = _stats.Deaths;

            // The bot is the one input driver for the whole session: take
            // InputHumanizer's lock up front (waiting briefly for a plugin that
            // still holds it) instead of per click, so nothing can slip a cursor
            // move between a hover and its click.
            if (!await _input.BeginSession(TimeSpan.FromSeconds(10), ct))
            {
                var why = _input.IsAvailable
                    ? "another plugin kept the InputHumanizer lock for 10s"
                    : "InputHumanizer is not available";
                _log($"Cannot start: {why}.");
                _webhook.NotifyCritical("Bot error", $"Could not take the input lock -- {why}.");
                CurrentState = BotState.Error;
                return false;
            }

            _strategy.Reset();
            _chores.ArmOmenRestock(); // a new session looks in the restock tab again
            CurrentState = DetectInitialState();
            _log($"Started: {_strategy.Name} ({CurrentState}).");

            while (!ct.IsCancellationRequested && CurrentState != BotState.Error)
            {
                // The one unconditional yield -- nothing below may starve the game thread.
                await TaskUtils.NextFrame();

                // Recovery runs BEFORE the loading gate: on the login path after a
                // disconnect the host's cached IsLoading is not contractual, and
                // neither the detection nor the Disconnected handler may be gated
                // off by it.
                if (_recovery.ShouldRecover(CurrentState, out var recoveryState))
                {
                    _log($"Recovery triggered: {CurrentState} -> {recoveryState}");
                    // ShouldRecover fires once per transition (never while already
                    // in a recovery state), so this counts each death exactly once.
                    if (recoveryState == BotState.Dead)
                    {
                        _stats.Deaths++;
                        NoteDeathForRun();
                        // The death may have consumed the Omen of Amelioration.
                        _chores.ArmOmenRestock();
                        _omenTripAfterDeath = true;
                    }
                    await _navigation.ReleaseAllKeys();
                    CurrentState = recoveryState;
                }

                // Loading screens pause the loop -- except while disconnected,
                // where the handler itself must keep driving the login flow.
                if (_gameState.IsLoading && CurrentState != BotState.Disconnected)
                {
                    WatchLongLoad();
                    continue;
                }
                _loadingSince = Clock.Never;

                if (await HoldWhileUnfocused())
                    continue;

                WatchProgress();

                // Each handler step runs under its own linked token so the
                // plugin's Tick can interrupt a mid-await handler (death or
                // disconnect during a long walk/loot/stash action) without
                // killing the engine. On interrupt the loop just re-evaluates —
                // the recovery branch above performs the transition.
                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _stepCts = stepCts;
                try
                {
                    var stepCt = stepCts.Token;
                    CurrentState = CurrentState switch
                    {
                        BotState.Idle => DoIdle(),
                        BotState.InHideout => DoInHideout(),
                        BotState.Stashing => await DoStashing(stepCt),
                        BotState.PreparingMap => await DoPreparingMap(stepCt),
                        BotState.WaitingForPortal => await DoWaitingForPortal(stepCt),
                        BotState.EnteringMap => await DoEnteringMap(stepCt),
                        BotState.RunningFarm => await DoRunningFarm(stepCt),
                        BotState.ReturningToHideout => await DoReturningToHideout(stepCt),
                        BotState.Dead => await DoDead(stepCt),
                        BotState.Disconnected => await DoDisconnected(stepCt),
                        BotState.Finished => BotState.Finished,
                        _ => BotState.Error
                    };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Step interrupted (recovery or lost focus) — state unchanged;
                    // the next iteration's ShouldRecover / focus gate takes over.
                    // The cut-short step may have left Ctrl/Shift down too.
                    await _navigation.ReleaseAllKeys();
                }
                finally
                {
                    _stepCts = null;
                }

                if (CurrentState == BotState.Finished)
                {
                    _log($"Finished: {Context.RunsCompleted} runs completed.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* Clean shutdown */ }
        catch (Exception ex)
        {
            // Full ToString: an overnight "Object reference not set" without the
            // stack is undiagnosable — the stack IS the diagnosis.
            _log($"CRITICAL ENGINE FATAL: {ex}");
            _webhook.NotifyCritical("Bot error",
                $"Engine crashed: {ex.GetType().Name}: {ex.Message} (full stack in Logs\\Info*.log)");
            CurrentState = BotState.Error;
            return false;
        }
        finally
        {
            await _navigation.ReleaseAllKeys();
            _input.EndSession();
        }

        if (ct.IsCancellationRequested)
            _webhook.NotifyCritical("Bot stopped", "Stopped manually.");
        else if (CurrentState == BotState.Finished)
            SendSessionSummary();
        else if (CurrentState == BotState.Error)
            _webhook.NotifyCritical("Bot error", "Engine entered the Error state.");

        _log($"Stopped ({CurrentState}).");
        return CurrentState != BotState.Error;
    }

    /// <summary>
    /// Snapshot the counters a run summary is computed against. Called wherever a
    /// run starts or is adopted; deliberately NOT on portal re-entries (same run).
    /// </summary>
    private void BeginRunTracking()
    {
        _runStartedAt = Clock.NowMs;
        _runStartDeaths = _stats.Deaths;
        _runStartGems = _stats.GemsLeveled;
        _repeatedDeathsNotified = false;
        // A run starting (activated, adopted, or entered mid-map) accounts for
        // any portals an interrupted preparation opened — never let that
        // snapshot outlive it and vouch for a LATER run's portals.
        _portalsBeforePreparation = null;
    }

    /// <summary>
    /// Focus gate: with the game window out of front, every key and click would
    /// land in whatever window is there now (unless background input is on, which
    /// goes straight to the game). Holds the loop (the plugin's Tick has already
    /// interrupted any running step) until focus is back, then the state's handler
    /// resumes from the top. True = holding this frame.
    /// </summary>
    private async SyncTask<bool> HoldWhileUnfocused()
    {
        if (!FocusGateApplies(CurrentState) || InputReachesGame)
        {
            if (_unfocusedSince != Clock.Never)
            {
                _log($"Game window is back in front -- resuming ({CurrentState}).");
                _unfocusedSince = Clock.Never;
                _unfocusedNotified = false;
            }
            return false;
        }

        _progressAt = Clock.NowMs; // a pause is not a stall
        if (_unfocusedSince == Clock.Never)
        {
            _unfocusedSince = Clock.NowMs;
            _log("Game window lost focus -- pausing until it is back in front.");
            await _navigation.ReleaseAllKeys();
        }
        else if (!_unfocusedNotified && Clock.SecondsSince(_unfocusedSince) > UnfocusedAlertSeconds)
        {
            _unfocusedNotified = true;
            _webhook.NotifyCritical("Paused: game not focused",
                $"The game window has been out of focus for {UnfocusedAlertSeconds / 60} minutes -- the bot " +
                "is paused (no input can reach the game). Bring the game window to the front to resume.");
        }

        return true;
    }

    /// <summary>A loading screen that never ends is a hang the loop cannot see
    /// (it just keeps waiting). Alert once; recovery still owns disconnects.</summary>
    private void WatchLongLoad()
    {
        _progressAt = Clock.NowMs; // loading is not a stall of ours
        if (_loadingSince == Clock.Never)
        {
            _loadingSince = Clock.NowMs;
            _longLoadNotified = false;
            return;
        }

        if (!_longLoadNotified && Clock.SecondsSince(_loadingSince) > LongLoadAlertSeconds)
        {
            _longLoadNotified = true;
            _log($"Loading screen for over {LongLoadAlertSeconds / 60} minutes ({CurrentState}).");
            _webhook.NotifyCritical("Stuck loading",
                $"A loading screen has lasted over {LongLoadAlertSeconds / 60} minutes ({CurrentState}). " +
                "The bot keeps waiting; the game may need a hand.");
        }
    }

    /// <summary>
    /// Engine-wide watchdog. Every handler bounds itself, but handlers chained
    /// together can still cycle without getting anywhere — state and phase
    /// changes are exactly what such a cycle produces, so they do NOT count.
    /// Progress is an outcome: a portal used, a wave started, a run completed,
    /// a death. Skipped in the states that alert on their own (Idle,
    /// Disconnected, Dead). Alert-only: the strategy's abandon timer stays in
    /// charge of ending a run.
    /// </summary>
    private void WatchProgress()
    {
        if (CurrentState is BotState.Idle or BotState.Disconnected or BotState.Dead)
        {
            _progressAt = Clock.NowMs;
            return;
        }

        var hash = HashCode.Combine(Context.PortalsUsed, Context.WavesStarted,
            Context.RunsCompleted, _stats.Deaths);
        if (hash != _progressHash)
        {
            _progressHash = hash;
            _progressAt = Clock.NowMs;
            _noProgressNotified = false;
            return;
        }

        if (_noProgressNotified || Clock.SecondsSince(_progressAt) < NoProgressAlertMinutes * 60)
            return;

        _noProgressNotified = true;
        _log($"No progress for {NoProgressAlertMinutes} minutes ({CurrentState} / {_strategy.CurrentPhaseName}).");
        _webhook.NotifyCritical("No progress",
            $"Nothing has changed for {NoProgressAlertMinutes} minutes (state {CurrentState}, " +
            $"phase {_strategy.CurrentPhaseName}). The bot keeps going, but may be stuck.");
    }

    /// <summary>Several deaths in one run usually mean something is off (build,
    /// ReAgent rules, a bad map mod) — say so once per run.</summary>
    private void NoteDeathForRun()
    {
        var deathsThisRun = _stats.Deaths - _runStartDeaths;
        if (!Context.RunActive || _repeatedDeathsNotified || deathsThisRun < RepeatedDeathsPerRun)
            return;

        _repeatedDeathsNotified = true;
        _webhook.NotifyCritical("Repeated deaths",
            $"{deathsThisRun} deaths in the current run ({Context.PortalsRemaining} portals left). " +
            "Re-entering continues -- check the build / ReAgent rules if this keeps happening.");
    }

    /// <summary>Idle = started (or landed) somewhere the bot has no route from.
    /// Re-detects every frame as before, but says so instead of waiting silently.</summary>
    private BotState DoIdle()
    {
        // Idle never has a run of ours to finish -- an armed stop applies now.
        if (StopAfterRun && !Context.RunActive)
            return StopAsRequested();

        var next = DetectInitialState();
        if (next != BotState.Idle)
        {
            _idleSince = Clock.Never;
            _idleNotified = false;
            return next;
        }

        if (_idleSince == Clock.Never)
        {
            _idleSince = Clock.NowMs;
            _log($"Waiting: not in the hideout, a town or the farm area ('{_gameState.AreaName}').");
        }
        else if (!_idleNotified && Clock.SecondsSince(_idleSince) > IdleAlertSeconds)
        {
            _idleNotified = true;
            _webhook.NotifyCritical("Bot idle",
                $"Waiting for {IdleAlertSeconds / 60} minutes in '{_gameState.AreaName}' -- the bot only " +
                "starts from the hideout, a town or the farm area.");
        }

        return BotState.Idle;
    }

    /// <summary>The armed "stop after this run" ends the session (Finished, so
    /// the session summary goes out and tells the user it is done).</summary>
    private BotState StopAsRequested()
    {
        _log("No run in progress -- stopping as requested (stop after this run).");
        _finishReason = "Stopped after the current run (requested).";
        return BotState.Finished;
    }

    /// <summary>Thin wrapper so a death give-up (Finished) carries a session
    /// summary reason, like <see cref="DoDisconnected"/>.</summary>
    private async SyncTask<BotState> DoDead(CancellationToken ct)
    {
        var next = await _recovery.HandleDeath(ct);
        if (next == BotState.Finished)
            _finishReason = "Stuck dead: no resurrect option worked.";
        return next;
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";

    private void SendRunSummary()
    {
        var lines = new List<string>();
        foreach (var (label, value) in _strategy.GetStatusRows(Context))
            lines.Add($"{label}: {value}");
        lines.Add($"Duration: {FormatDuration(TimeSpan.FromMilliseconds(Clock.MsSince(_runStartedAt)))}");
        lines.Add($"Deaths: {_stats.Deaths - _runStartDeaths}");
        lines.Add($"Portals used: {Context.PortalsUsed}/{RunContext.MaxPortals}");
        lines.Add($"Gems leveled: {_stats.GemsLeveled - _runStartGems}");

        var title = Context.AbandonedByTimer
            ? $"Run #{Context.RunsCompleted} abandoned (time limit)"
            : $"Run #{Context.RunsCompleted} complete";
        _webhook.NotifySummary(title, string.Join("\n", lines));
    }

    private void SendSessionSummary()
    {
        var elapsed = TimeSpan.FromMilliseconds(Clock.MsSince(_sessionStartedAt));
        var runsPerHour = elapsed.TotalHours > 0 ? Context.RunsCompleted / elapsed.TotalHours : 0;
        var lines = new List<string>
        {
            $"Runs completed: {Context.RunsCompleted}",
            $"Deaths: {_stats.Deaths - _sessionStartDeaths}",
            $"Session time: {FormatDuration(elapsed)}",
            $"Runs/hour: {runsPerHour:F1}"
        };
        if (!string.IsNullOrEmpty(_finishReason))
            lines.Add($"Reason: {_finishReason}");

        _webhook.NotifySummary("Session finished", string.Join("\n", lines));
    }

    private BotState DetectInitialState()
    {
        if (_gameState.IsInHideout) return BotState.InHideout;
        if (_strategy.IsFarmArea(_gameState.AreaName))
        {
            // Bot was started mid-map: treat as an active run with one portal spent.
            if (!Context.RunActive)
            {
                Context.ResetForNewRun();
                Context.RunActive = true;
                Context.PortalsUsed = 1;
                BeginRunTracking();
            }
            return BotState.RunningFarm;
        }
        if (_gameState.IsInTown) return BotState.ReturningToHideout;
        return BotState.Idle;
    }

    /// <summary>
    /// Reality check for the hideout-side states: a portal entry whose area
    /// transition was never confirmed (loading screen outlasted the check, stale
    /// host) leaves the engine cycling InHideout/WaitingForPortal while the
    /// player already stands INSIDE the farm area -- and neither state looks at
    /// the actual area, so nothing would ever correct it (only Idle re-detects).
    /// When that happens, count the consumed portal and hand control to the
    /// strategy, mirroring the confirmed-entry path in DoEnteringMap.
    /// </summary>
    private bool AdoptUnconfirmedMapEntry()
    {
        if (!_gameState.IsReady || !_strategy.IsFarmArea(_gameState.AreaName))
            return false;

        if (!Context.RunActive)
        {
            Context.ResetForNewRun();
            Context.RunActive = true;
            BeginRunTracking();
        }

        // The entry consumed a portal even though its transition was missed.
        Context.PortalsUsed++;
        _portalEntryFailuresInARow = 0;
        _portalEntryTroubleNotified = false;
        var firstEntry = Context.PortalsUsed == 1;
        // We entered moments ago and have not walked anywhere on our own -- the
        // current position is the return-portal spot (kept from the first entry
        // on re-entries: same instance, same portals).
        Context.MapEntryPoint ??= _gameState.PlayerGridPos;
        _log($"Already inside '{_gameState.AreaName}' -- adopting the unconfirmed entry " +
             $"(portal {Context.PortalsUsed}/{RunContext.MaxPortals}).");
        if (firstEntry)
            _strategy.Reset();
        return true;
    }

    /// <summary>
    /// A standing tab-settings problem WITH a full inventory: the config latch
    /// keeps the stash chores off (retrying can't fix a wrong tab name), but
    /// farming on in this state means no loot gets picked — stop loudly
    /// instead. (The latch lifts on its own when the settings change.)
    /// Null when the inventory still has room (farming on is acceptable).
    /// </summary>
    private BotState? StopForConfigErrorWithFullInventory()
    {
        if (!_chores.UrgentStashPending) return null;

        var detail = _chores.LastFailureDetail ?? "A configured stash tab was not found.";
        _log($"Stash settings problem with a FULL inventory -- stopping. {detail}");
        _webhook.NotifyCritical("Stash settings problem",
            $"{detail} The inventory is full, so farming on cannot pick loot -- session stopped. " +
            "Fix the tab settings and restart.");
        _finishReason = "Stash settings problem with a full inventory.";
        return BotState.Finished;
    }

    private BotState DoInHideout()
    {
        // One pass per death: the trip is decided here or not at all. Without a
        // live run the regular chores below fetch the omen anyway.
        var omenTrip = _omenTripAfterDeath;
        _omenTripAfterDeath = false;
        _omenOnlyTrip = false;

        // Standing inside the farm area means an entry went unconfirmed -- no
        // hideout logic applies; hand the run to the strategy.
        if (AdoptUnconfirmedMapEntry())
            return BotState.RunningFarm;

        // Farm portals already up (e.g. manually activated, or bot restarted mid-run):
        // adopt them instead of consuming a fresh fragment. Used count is inferred
        // from how many of the 6 portals remain. Skipped when they belong to a run
        // we just completed -- those lead back into a finished map.
        if (!Context.RunActive && !_areas.IgnorePortalsOfCompletedRun)
        {
            var existing = _gameState.FindFarmPortals(_strategy.IsFarmArea);
            if (existing.Count > 0)
            {
                Context.ResetForNewRun();
                Context.RunActive = true;
                Context.PortalsUsed = Math.Max(0, RunContext.MaxPortals - existing.Count);
                BeginRunTracking();
                _log($"Adopting existing run -- {existing.Count} portals up.");
            }
        }

        // A run is still live (death/stash trip mid-run) -> go back through the portals.
        if (Context.RunActive)
        {
            if (Context.PortalsRemaining > 0)
            {
                // Emptying the inventory is the whole reason a strategy asks for
                // a hideout trip mid-run (its in-area stash could not be opened).
                // Re-entering before the dump would send the bot straight back
                // into the deadlock it just spent a portal to escape — so the
                // chores run FIRST and the re-entry happens on the next pass.
                // UrgentStashPending covers the same need for the paths that
                // never raised the flag (a death, a manual restart).
                // A requested trip ignores the chore backoff: it was paid for with
                // a portal and costs exactly one attempt (DoStashing consumes the
                // flag) — skipping it would re-enter still full.
                if (Context.StashTripRequested ||
                    (_chores.UrgentStashPending && Clock.Reached(_stashBackoffUntil)))
                {
                    if (_chores.ConfigErrorStanding && StopForConfigErrorWithFullInventory() is { } stopNow)
                        return stopNow;

                    _log("Run still active but the inventory needs emptying -- stashing before re-entering.");
                    return BotState.Stashing;
                }

                // The death consumed the Omen of Amelioration: fetch a new one
                // before going back in -- the omen ONLY (no fragment restock, no
                // dump, no gems). Exactly one attempt (the flag is gone after
                // this pass), so a failed fetch cannot hold the re-entry hostage.
                // The chores between runs try again.
                if (omenTrip && Clock.Reached(_stashBackoffUntil) &&
                    _chores.OmenRestockPending(_strategy.RestockTabName))
                {
                    _log("Died -- fetching a new Omen of Amelioration before re-entering.");
                    _omenOnlyTrip = true;
                    return BotState.Stashing;
                }

                _log($"Run still active, {Context.PortalsRemaining} portals left. Re-entering.");
                _portalWaitStartedAt = Clock.NowMs;
                return BotState.WaitingForPortal;
            }

            _log("Run abandoned -- no portals left.");
            _webhook.NotifyCritical("Run abandoned", StopAfterRun
                ? "No portals left to re-enter -- stopping as requested (stop after this run)."
                : "No portals left to re-enter -- starting a fresh run.");
            Context.RunActive = false;
        }

        // No run is live past this point -- the requested stop lands here.
        if (StopAfterRun)
            return StopAsRequested();

        if (_chores.ConfigErrorStanding && StopForConfigErrorWithFullInventory() is { } stop)
            return stop;

        // Chores (stash dump, gem level-ups, fragment restock) that
        // keep failing must not loop the InHideout<->Stashing states forever --
        // back off (escalating) and farm on.
        if (Clock.Reached(_stashBackoffUntil) &&
            _chores.HasPending(_strategy.MapDeviceItemPaths, _strategy.RestockTabName))
            return BotState.Stashing;

        var maxRuns = _getMaxRuns();
        if (maxRuns > 0 && Context.RunsCompleted >= maxRuns)
        {
            _log($"Reached configured run limit ({maxRuns}).");
            _finishReason = $"Run limit reached ({maxRuns}).";
            return BotState.Finished;
        }

        return BotState.PreparingMap;
    }

    private async SyncTask<BotState> DoStashing(CancellationToken ct)
    {
        if (_omenOnlyTrip)
            return await DoOmenOnlyTrip(ct);

        BotLog.Step("Running hideout chores (stash / gems / fragments / omen)...");

        // A strategy that came home ONLY to empty its inventory gets the dump
        // whether or not the last cell is taken. Consumed once the attempt
        // RETURNS, so the request costs exactly one attempt: if that attempt
        // cannot fix it, the run resumes instead of looping the hideout states.
        // (An attempt cut short — lost focus — is re-run with the flag intact.)
        var forceStash = Context.StashTripRequested;

        var result = await _chores.Run(
            _strategy.MapDeviceItemPaths, _strategy.RestockTabName, ct, forceStash);
        Context.StashTripRequested = false;
        switch (result)
        {
            case ChoresResult.OutOfFragments:
                // The next fragment is only needed for the NEXT run -- a live run
                // with portals left is finished first.
                if (Context.RunActive && Context.PortalsRemaining > 0)
                {
                    _log("Fragment tab reads empty, but the current run is still live -- finishing it first.");
                    break;
                }

                if (!_fragmentTabReadEmpty)
                {
                    _fragmentTabReadEmpty = true;
                    _log("Fragment tab reads empty -- reopening the stash to confirm before ending the session.");
                    await Task.Delay(2000, ct);
                    break;
                }

                _log("Fragment input tab is empty (confirmed twice) -- farming session complete.");
                _finishReason = "Fragment input tab empty.";
                return BotState.Finished;

            case ChoresResult.Done:
                _fragmentTabReadEmpty = false;
                _stashFailuresInARow = 0;
                _stashBackoffLevel = 0;
                _stashTroubleNotified = false;
                _restockRetries = 0;
                break;

            case ChoresResult.ConfigError:
            {
                // A wrong tab name never fixes itself by waiting — alert once and
                // pause the chores (HasPending) until the settings change. With a
                // FULL inventory even farming on is pointless: loot can no longer
                // be picked, so stop the session instead.
                _stashFailuresInARow = 0;
                if (StopForConfigErrorWithFullInventory() is { } stop)
                    return stop;

                var detail = _chores.LastFailureDetail ?? "A configured stash tab was not found.";
                _log($"Chores blocked by a settings problem -- {detail} Paused until the settings change.");
                _webhook.NotifyCritical("Stash settings problem",
                    $"{detail} Stashing is paused until the settings change -- retrying cannot fix a wrong tab name.");
                break;
            }

            default:
            {
                _stashFailuresInARow++;
                var detail = _chores.LastFailureDetail ?? "unknown step";

                // Pace the first retries: a transient failure (entities still
                // streaming in, UI settling) must not burn all attempts within a
                // few frames.
                if (_stashFailuresInARow < MaxStashFailures)
                {
                    _log($"Chores failed ({detail}) -- retrying shortly.");
                    await Task.Delay(2000, ct);
                    break;
                }

                if (_chores.UrgentStashPending)
                {
                    // NEVER back off with a full inventory — every backed-off wave
                    // farms blind (loot cannot be picked). Keep retrying, alert
                    // once, and stop loudly when it clearly will not recover.
                    if (_stashFailuresInARow >= MaxUrgentStashFailures)
                    {
                        _log($"Stash unreachable with a FULL inventory ({_stashFailuresInARow}x) -- stopping.");
                        _webhook.NotifyCritical("Stash unreachable",
                            $"Stash chores failed {_stashFailuresInARow}x in a row with a FULL inventory " +
                            $"({detail}) -- stopping the session so it does not farm blind for hours.");
                        _finishReason = "Stash unreachable with a full inventory.";
                        return BotState.Finished;
                    }

                    if (!_stashTroubleNotified)
                    {
                        _stashTroubleNotified = true;
                        _webhook.NotifyCritical("Stash trouble",
                            $"Stash chores keep failing ({detail}) and the inventory is FULL -- " +
                            "retrying instead of backing off; will stop if it cannot recover.");
                    }

                    _log($"Chores failed {_stashFailuresInARow}x with a full inventory ({detail}) -- retrying (no backoff while full).");
                    await Task.Delay(2000, ct);
                    break;
                }

                // Escalating backoff (30s → 60s → 120s) instead of a flat 5 minutes:
                // transient causes clear in seconds; persistent ones alert via
                // webhook with the failing step so the settings can be fixed.
                var backoffSeconds = StashBackoffSeconds[Math.Min(_stashBackoffLevel, StashBackoffSeconds.Length - 1)];
                _stashBackoffLevel++;
                _stashFailuresInARow = 0;
                _stashBackoffUntil = Clock.InSeconds(backoffSeconds);
                _log($"Chores failed {MaxStashFailures}x in a row ({detail}) -- backing off for {backoffSeconds}s.");
                if (!_stashTroubleNotified)
                {
                    _stashTroubleNotified = true;
                    _webhook.NotifyCritical("Stash trouble",
                        $"Stash chores failed {MaxStashFailures}x in a row ({detail}) -- backing off for " +
                        $"{backoffSeconds}s (escalating). Loot dumps and fragment restocks are delayed.");
                }

                break;
            }
        }

        return BotState.InHideout;
    }

    /// <summary>
    /// The death trip: one attempt at the omen, then back to the hideout pass,
    /// which re-enters the live run whatever the outcome. Deliberately outside
    /// the chore failure counters and backoff — those pace the full chores, and
    /// one failed omen pull must not delay the next loot dump.
    /// </summary>
    private async SyncTask<BotState> DoOmenOnlyTrip(CancellationToken ct)
    {
        BotLog.Step("Fetching an Omen of Amelioration (omen only)...");
        var result = await _chores.RestockOmenOnly(_strategy.RestockTabName, ct);
        _omenOnlyTrip = false;
        switch (result)
        {
            case ChoresResult.Failed:
                _log($"Could not fetch an Omen of Amelioration ({_chores.LastFailureDetail ?? "unknown step"}) " +
                     "-- re-entering without one; the chores between runs try again.");
                break;

            case ChoresResult.ConfigError:
            {
                var detail = _chores.LastFailureDetail ?? "The restock tab was not found.";
                _log($"Omen fetch blocked by a settings problem -- {detail} Re-entering without one.");
                _webhook.NotifyCritical("Stash settings problem",
                    $"{detail} Stashing is paused until the settings change -- retrying cannot fix a wrong tab name.");
                break;
            }
        }

        return BotState.InHideout;
    }

    private async SyncTask<BotState> DoPreparingMap(CancellationToken ct)
    {
        if (_strategy.MapDeviceItemPaths.Count == 0)
        {
            // Legacy strategies: portals appear without our involvement.
            _portalWaitStartedAt = Clock.NowMs;
            return BotState.WaitingForPortal;
        }

        // A preparation cut short AFTER its Activate click (lost focus during the
        // portal wait or the UI-close waits) is re-run from the top — which would
        // slot and burn a second fragment while the first run's portals stand
        // unused. The farm portals standing when the preparation began are kept
        // across such re-runs: anything new means the activation already happened.
        MapDeviceResult result;
        if (_portalsBeforePreparation is { } before &&
            _gameState.FindFarmPortals(_strategy.IsFarmArea).Any(p => !before.Contains(p.Id)))
        {
            _log("The interrupted activation already opened new portals -- not activating again.");
            result = MapDeviceResult.Success;
        }
        else
        {
            _portalsBeforePreparation ??= _gameState.FindFarmPortals(_strategy.IsFarmArea)
                .Select(p => p.Id)
                .ToHashSet();
            BotLog.Step("Preparing map device...");
            result = await _mapDevice.PrepareAndActivate(
                _strategy.MapDeviceItemPaths, _strategy.IsFarmArea, ct);
        }

        _portalsBeforePreparation = null; // this preparation returned (any result)

        switch (result)
        {
            case MapDeviceResult.Success:
                Context.ResetForNewRun();
                Context.RunActive = true;
                BeginRunTracking();
                _restockRetries = 0;
                _mapDeviceFailuresInARow = 0;
                _mapDeviceTroubleNotified = false;
                _portalEntryFailuresInARow = 0;
                _portalEntryTroubleNotified = false;
                _areas.IgnorePortalsOfCompletedRun = false; // fresh portals from here on
                _portalWaitStartedAt = Clock.NowMs;
                return BotState.WaitingForPortal;

            case MapDeviceResult.NoFragments:
                // "Nothing on hand" is only final when no stash restock exists to
                // fix it. After a failed/backed-off stash session the fragments
                // usually still sit in the tab -- lift the backoff and give the
                // chores another (bounded) chance instead of ending the session
                // on a false "out of fragments". The true tab-empty signal is
                // ChoresResult.OutOfFragments, handled in DoStashing.
                if (_chores.CanRestock(_strategy.MapDeviceItemPaths, _strategy.RestockTabName))
                {
                    if (_restockRetries < MaxRestockRetries)
                    {
                        _restockRetries++;
                        _stashBackoffUntil = 0;
                        _log($"No fragments on hand -- retrying the stash restock ({_restockRetries}/{MaxRestockRetries}).");
                        await Task.Delay(2000, ct);
                        return BotState.InHideout;
                    }

                    _log("No fragments on hand and the stash restock keeps failing -- stopping.");
                    _finishReason = "Stash restock kept failing (fragments may remain in the tab).";
                    return BotState.Finished;
                }

                _log("Out of fragments -- farming session complete.");
                _finishReason = "Out of fragments.";
                return BotState.Finished;

            default:
            {
                // Same failsafe shape as the stash chores: reset the UI between
                // attempts (a stale half-open Atlas or a reward popup swallows
                // clicks), escalate the pacing, alert once via webhook, and stop
                // the session instead of silently retrying for hours.
                _mapDeviceFailuresInARow++;
                var detail = _mapDevice.LastFailure ?? "unknown step";

                // A device failure while demonstrably NOT in the hideout is a
                // routing problem, not a device problem — go home through the
                // existing machinery instead of retrying in the wrong area.
                // (Still counted, so a broken route cannot loop forever.)
                if (_mapDeviceFailuresInARow < MaxMapDeviceFailures &&
                    _gameState.IsReady && !_gameState.IsInHideout)
                {
                    _log($"Map device flow failed outside the hideout ('{_gameState.AreaName}') -- routing home first.");
                    return BotState.ReturningToHideout;
                }

                if (_mapDeviceFailuresInARow >= MaxMapDeviceFailures)
                {
                    _log($"Map device flow failed {_mapDeviceFailuresInARow}x in a row ({detail}) -- stopping.");
                    _webhook.NotifyCritical("Map device unreachable",
                        $"Opening a map failed {_mapDeviceFailuresInARow}x in a row ({detail}) -- " +
                        "stopping the session. Check the hideout/device manually.");
                    _finishReason = "Map device flow kept failing.";
                    return BotState.Finished;
                }

                if (_mapDeviceFailuresInARow == 3 && !_mapDeviceTroubleNotified)
                {
                    _mapDeviceTroubleNotified = true;
                    _webhook.NotifyCritical("Map device trouble",
                        $"Opening a map has failed 3x in a row ({detail}) -- hard-resetting " +
                        $"(UI + area refresh + fresh approach) and retrying; " +
                        $"will stop after {MaxMapDeviceFailures} failures. Details are in Logs\\Info*.log.");
                }

                // First failure: quick UI reset. From the second on, restart
                // everything the flow depends on (UI + host area refresh + a
                // fresh approach walk) — repeated failures with a fine-looking
                // hideout are usually the server not sending an update or the
                // host reading stale state, which no UI reset can fix.
                if (_mapDeviceFailuresInARow >= 2)
                {
                    _log($"Map device flow failed ({detail}) -- hard reset (UI + area refresh + fresh approach), then retrying.");
                    await _mapDevice.HardReset(ct);
                }
                else
                {
                    _log($"Map device flow failed ({detail}) -- resetting the UI and retrying shortly.");
                    await _mapDevice.ResetUi(ct);
                }

                await Task.Delay(_mapDeviceFailuresInARow >= 3 ? 10000 : 2000, ct);
                return BotState.InHideout;
            }
        }
    }

    private async SyncTask<BotState> DoWaitingForPortal(CancellationToken ct)
    {
        // Inside the farm area there are no hideout-side farm portals to find --
        // an unconfirmed entry would otherwise ping-pong this state with
        // InHideout forever.
        if (AdoptUnconfirmedMapEntry())
            return BotState.RunningFarm;

        var portals = _gameState.FindFarmPortals(_strategy.IsFarmArea);
        if (portals.Count > 0)
            return BotState.EnteringMap;

        if (_portalWaitStartedAt == Clock.Never)
            _portalWaitStartedAt = Clock.NowMs;

        if (Clock.SecondsSince(_portalWaitStartedAt) > 15)
        {
            _portalWaitStartedAt = Clock.Never;
            if (Context.RunActive && Context.PortalsUsed > 0)
            {
                _log("No portals found for re-entry -- run lost.");
                _webhook.NotifyCritical("Run lost", "No portals found for re-entry.");
                Context.RunActive = false;
            }
            return BotState.InHideout;
        }

        await Task.Delay(500, ct);
        return BotState.WaitingForPortal;
    }

    private async SyncTask<BotState> DoEnteringMap(CancellationToken ct)
    {
        var portals = _gameState.FindFarmPortals(_strategy.IsFarmArea);
        if (portals.Count == 0)
        {
            _portalWaitStartedAt = Clock.NowMs;
            return BotState.WaitingForPortal;
        }

        // The host skips its area refresh when an area change comes less than
        // ~5 s after the previous one. A death re-entry (checkpoint resurrect,
        // then straight back in) used to land 2-4 s after the hideout arrival,
        // so the map ran on unrefreshed host data. On 2026-11-17 that left the
        // return portal unclickable for the whole retry budget. Let the hideout
        // arrival age past the window before clicking.
        var settle = MinSecondsBetweenAreaChanges - _gameState.SecondsSinceAreaChange;
        if (settle > 0)
        {
            BotLog.Step($"Letting the host settle the area change ({settle:0.0}s) before entering...");
            await Task.Delay(TimeSpan.FromSeconds(settle), ct);
            return BotState.EnteringMap; // re-read the portals after the wait
        }

        var portal = portals[0];
        BotLog.Step($"Entering portal ({portal.GetComponent<ExileCore.PoEMemory.Components.Render>()?.Name ?? "?"})...");
        var clicked = await _navigation.ClickEntity(portal, ct);

        // Wait for the transition to land in a farm area. A click that never went
        // out cannot transition — skip the 12 s wait and its forced area refresh.
        if (clicked && await WaitForAreaTransition(
                () => _gameState.IsReady && _strategy.IsFarmArea(_gameState.AreaName), 12, ct))
        {
            Context.PortalsUsed++;
            _portalEntryFailuresInARow = 0;
            _portalEntryTroubleNotified = false;
            // We stand at the return portals right now -- remember the spot.
            Context.MapEntryPoint = _gameState.PlayerGridPos;
            var firstEntry = Context.PortalsUsed == 1;
            _log($"Entered '{_gameState.AreaName}' (portal {Context.PortalsUsed}/{RunContext.MaxPortals}).");
            if (firstEntry)
                _strategy.Reset();
            return BotState.RunningFarm;
        }

        return await PortalEntryFailed(clicked
            ? "the click did not lead into the map"
            : "the portal could not be clicked", ct);
    }

    /// <summary>
    /// Same failsafe shape as the map device: WaitingForPortal hands straight back
    /// to EnteringMap while a portal is visible, so without a count a portal that
    /// keeps refusing would loop forever. Reset the UI between attempts (a panel
    /// swallowing the click is the usual cause), alert once, and stop loudly.
    /// </summary>
    private async SyncTask<BotState> PortalEntryFailed(string why, CancellationToken ct)
    {
        _portalEntryFailuresInARow++;

        if (_portalEntryFailuresInARow >= MaxPortalEntryFailures)
        {
            _log($"Portal entry failed {_portalEntryFailuresInARow}x in a row ({why}) -- stopping.");
            _webhook.NotifyCritical("Portal entry failing",
                $"Entering the map failed {_portalEntryFailuresInARow}x in a row ({why}) -- stopping the " +
                "session. Check the hideout portals manually.");
            _finishReason = "Portal entry kept failing.";
            return BotState.Finished;
        }

        if (_portalEntryFailuresInARow == 3 && !_portalEntryTroubleNotified)
        {
            _portalEntryTroubleNotified = true;
            _webhook.NotifyCritical("Portal trouble",
                $"Entering the map has failed 3x in a row ({why}) -- resetting the UI and retrying; " +
                $"will stop after {MaxPortalEntryFailures} failures.");
        }

        _log($"Portal entry failed ({why}) -- attempt {_portalEntryFailuresInARow}/{MaxPortalEntryFailures}, " +
             "resetting the UI and retrying.");
        await _mapDevice.ResetUi(ct);
        await Task.Delay(_portalEntryFailuresInARow >= 3 ? 5000 : 1000, ct);
        _portalWaitStartedAt = Clock.NowMs;
        return BotState.WaitingForPortal;
    }

    private async SyncTask<BotState> DoRunningFarm(CancellationToken ct)
    {
        // Landed outside the farm area with the engine still in RunningFarm
        // (manual relogin after a disconnect, stray portal) — the strategy must
        // not run here: its cached points belong to the map, so it would walk
        // toward map coordinates in the wrong zone forever. Route home; InHideout
        // re-enters through the remaining portals while the run is still live.
        if (_gameState.IsReady && !_strategy.IsFarmArea(_gameState.AreaName))
        {
            _log($"Not in the farm area anymore ('{_gameState.AreaName}') -- routing back.");
            await _navigation.ReleaseMoveKey();
            return BotState.ReturningToHideout;
        }

        // The strategy wants a fresh entry into the SAME run (stuck instance UI):
        // leave through a town portal with the run still active — DoInHideout then
        // re-enters through the remaining farm portals on its own.
        if (Context.ReEntryRequested)
        {
            Context.ReEntryRequested = false;
            _log("Strategy requested a portal re-entry -- leaving the map.");
            return BotState.ReturningToHideout;
        }

        // Stray-panel watchdog: a panel the bot never wants (MTX shop, passive
        // tree, ...) opened by a stray click/keypress swallows every hover and
        // click — left alone it stalls the run silently until the abandon timer.
        // Escape it here, before the strategy runs, throttled so a panel that
        // refuses to close cannot turn the loop into an Escape spammer.
        var strayPanelOpen = _gameState.HasStrayPanelOpen;
        if (strayPanelOpen)
        {
            if (Clock.SecondsSince(_strayPanelEscapeAt) > 3)
            {
                _strayPanelEscapeAt = Clock.NowMs;
                _strayPanelEscapes++;
                _log($"A stray panel (MTX shop / tree / map / social) is open -- pressing Escape to close it (attempt {_strayPanelEscapes}).");
                await _navigation.ReleaseMoveKey();
                await _input.PressKey(Keys.Escape, ct);

                // Escape not working means something INSIDE the panel holds focus
                // (e.g. a purchase-confirmation dialog) -- that must be visible in
                // Discord, not silently retried all night.
                if (_strayPanelEscapes >= 5 && !_strayPanelNotified)
                {
                    _strayPanelNotified = true;
                    _webhook.NotifyCritical("Stray panel will not close",
                        $"A panel (MTX shop / tree / map / social) is open in the map and {_strayPanelEscapes} " +
                        "Escape presses have not closed it. The bot keeps trying, but may need a hand.");
                }
            }

            // The panel owns the screen -- running the strategy under it would
            // just feed clicks into it (and could confirm a shop dialog). But
            // only hold the strategy back while Escape still has a chance:
            // past 10 failed presses, fall through so the strategy's abandon
            // timer keeps owning the endgame instead of looping here forever.
            if (_strayPanelEscapes < 10)
                return BotState.RunningFarm;
        }

        if (!strayPanelOpen && _strayPanelEscapes > 0 && Clock.SecondsSince(_strayPanelEscapeAt) > 3)
        {
            if (_strayPanelEscapes >= 5)
                _webhook.NotifyCritical("Stray panel closed", "The stuck panel is gone -- resuming the run.");
            _strayPanelEscapes = 0;
            _strayPanelNotified = false;
        }

        bool farmComplete = await _strategy.ExecuteFarm(Context, ct);

        if (farmComplete)
        {
            BotLog.Step("Farm objective complete!");
            Context.RunActive = false;
            Context.RunsCompleted++;
            _areas.IgnorePortalsOfCompletedRun = true; // leftovers lead into the finished map
            SendRunSummary();
            return BotState.ReturningToHideout;
        }

        return BotState.RunningFarm;
    }

    /// <summary>Thin wrapper so a reconnect give-up (Finished) carries a session
    /// summary reason -- the recovery service cannot reach _finishReason itself.</summary>
    private async SyncTask<BotState> DoDisconnected(CancellationToken ct)
    {
        var next = await _recovery.HandleDisconnect(ct);
        if (next == BotState.Finished)
            _finishReason = "Could not reconnect (relogin attempt limit reached).";
        return next;
    }

    private async SyncTask<BotState> DoReturningToHideout(CancellationToken ct)
    {
        if (_gameState.IsInHideout)
        {
            _log("Back in hideout.");
            _noReturnPortalNotified = false;
            _returnFailuresInARow = 0;
            _returnTroubleNotified = false;
            return BotState.InHideout;
        }

        // Town has no portals of ours -- the hideout keybind is the way home.
        if (_gameState.IsInTown)
        {
            if (!await _recovery.TryTownToHideout(ct))
                return await ReturnFailed("the hideout key did not leave town (is it bound to /hideout?)", ct);
            return BotState.ReturningToHideout;
        }

        // In the farm area: take the nearest town portal back. The host's list
        // order is arbitrary, so sort by distance.
        var playerPos = _gameState.PlayerGridPos;
        var portals = _gameState.GetTownPortals()
            .OrderBy(p => System.Numerics.Vector2.Distance(playerPos, p.GridPosNum))
            .ToList();
        if (portals.Count > 0)
        {
            BotLog.Step("Taking portal back to hideout...");
            var clicked = await _navigation.ClickEntity(portals[0], ct);

            if (clicked && await WaitForAreaTransition(
                    () => _gameState.IsInHideout || _gameState.IsInTown, 12, ct))
                return BotState.ReturningToHideout; // re-evaluates: hideout exits, town presses the key

            return await ReturnFailed(clicked
                ? "the portal click did not lead out of the map"
                : "the return portal could not be clicked", ct);
        }
        else if (Context.MapEntryPoint is { } entry &&
                 _strategy.IsFarmArea(_gameState.AreaName) &&
                 System.Numerics.Vector2.Distance(_gameState.PlayerGridPos, entry) > 30)
        {
            // The return portals stand at the map entry but stream in only when close.
            BotLog.Step("Walking back to the map entry point...");
            await _navigation.MoveToTarget(entry, ct, 25);
        }
        else if (_gameState.IsReady && _strategy.IsFarmArea(_gameState.AreaName))
        {
            // No portal in the entity bubble and no (useful) saved entry point —
            // standing still can never fix that. A stale host is ruled out first
            // (missed area change), then the exploration tour hunts the portal.
            // Bounded per pass so a genuinely portal-less map (all six consumed)
            // alerts instead of pacing silently forever.
            if (_gameState.TryForceRefreshArea())
                await Task.Delay(1000, ct);

            _log("No portal in sight -- exploring the area to find one.");
            var deadline = Clock.InMinutes(3);
            await _exploration.ExploreUntil(
                WaitHelpers.Throttle(
                    () => _gameState.GetTownPortals().Count > 0 || Clock.Reached(deadline), 250), ct);

            if (_gameState.GetTownPortals().Count == 0 && !_noReturnPortalNotified)
            {
                _noReturnPortalNotified = true;
                _webhook.NotifyCritical("Stuck in map",
                    "The run is over but no return portal was found even after exploring the area " +
                    "for 3 minutes. The search continues -- a manual portal scroll or logout may be needed.");
            }
        }
        else
        {
            _log("No portal back found -- waiting...");
            await Task.Delay(1000, ct);
        }

        return BotState.ReturningToHideout;
    }

    /// <summary>
    /// Failsafe for the way home (town portal click, hideout key): count, reset
    /// the UI between attempts, alert once, and stop loudly instead of retrying
    /// all night. Searching for a portal that is not in sight is not a failure —
    /// the exploration branch has its own alert.
    /// </summary>
    private async SyncTask<BotState> ReturnFailed(string why, CancellationToken ct)
    {
        _returnFailuresInARow++;

        if (_returnFailuresInARow >= MaxReturnFailures)
        {
            _log($"Getting back to the hideout failed {_returnFailuresInARow}x in a row ({why}) -- stopping.");
            _webhook.NotifyCritical("Cannot get home",
                $"Returning to the hideout failed {_returnFailuresInARow}x in a row ({why}) -- stopping " +
                $"the session in '{_gameState.AreaName}'.");
            _finishReason = "Returning to the hideout kept failing.";
            return BotState.Finished;
        }

        if (_returnFailuresInARow == 3 && !_returnTroubleNotified)
        {
            _returnTroubleNotified = true;
            _webhook.NotifyCritical("Trouble getting home",
                $"Returning to the hideout has failed 3x in a row ({why}) -- retrying; " +
                $"will stop after {MaxReturnFailures} failures.");
        }

        _log($"Return failed ({why}) -- attempt {_returnFailuresInARow}/{MaxReturnFailures}, retrying.");
        await _mapDevice.ResetUi(ct);

        // Retrying against the same host data repeats the same failure. On
        // 2026-11-17 the portal stood next to the character but projected
        // off-screen 10x in a row, while a manual click worked. From the second
        // failure, rebuild the host's caches (throttled to once per 10 s
        // inside) before the next attempt.
        if (_returnFailuresInARow >= 2 && _gameState.TryForceRefreshArea())
        {
            _log("Forced an area refresh to rule out stale host data before retrying.");
            await Task.Delay(1000, ct);
        }

        await Task.Delay(_returnFailuresInARow >= 3 ? 5000 : 1000, ct);
        return BotState.ReturningToHideout;
    }

    /// <summary>
    /// Frame-checked wait for a post-click area transition: skips loading frames
    /// and returns true as soon as the arrival condition holds. Loading screens
    /// do not consume the confirmation window -- a Simulacrum load alone can
    /// outlast it, and an expired wait here mis-reports a transition that DID
    /// happen (the engine then waits for portals while standing inside the map).
    /// A hard cap still bounds the whole wait so a stuck loading flag cannot
    /// hang the handler forever.
    /// </summary>
    private async SyncTask<bool> WaitForAreaTransition(
        Func<bool> arrived, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = Clock.InSeconds(timeoutSeconds);
        var hardCap = Clock.InMinutes(2);
        while (!Clock.Reached(deadline) && !ct.IsCancellationRequested)
        {
            await TaskUtils.NextFrame();
            if (_gameState.IsLoading)
            {
                if (!Clock.Reached(hardCap))
                    deadline = Clock.InSeconds(timeoutSeconds);
                continue;
            }

            if (arrived()) return true;
        }

        // Stopped or interrupted is not "timed out" — never force a host area
        // refresh (an AreaChange in every plugin) on the way out.
        ct.ThrowIfCancellationRequested();

        // Timed out: the transition may have HAPPENED without the host noticing
        // (missed area change leaves CurrentArea/entities stale while IsReady
        // looks fine). Force a re-read and give it one more chance.
        if (_gameState.TryForceRefreshArea())
        {
            _log("Transition not confirmed -- forced an area refresh to rule out a stale host.");
            await Task.Delay(1000, ct);
            if (arrived()) return true;
        }

        return false;
    }
}
