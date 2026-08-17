using System;
using System.Collections.Generic;
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
    private int _mapDeviceFailuresInARow;
    private bool _mapDeviceTroubleNotified;

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

    /// <summary>Cancellation for the CURRENTLY RUNNING state-handler step —
    /// linked to the master token. Cancelled from the plugin's Tick when a
    /// death/disconnect happens mid-handler (the engine loop itself cannot see
    /// it then: it only runs between handler steps).</summary>
    private CancellationTokenSource? _stepCts;

    /// <summary>Whether an external recovery interrupt is warranted right now.
    /// Polled from the plugin's Tick — the only code that still runs while a
    /// state handler is awaiting inside this SyncTask.</summary>
    public bool NeedsRecoveryInterrupt => _recovery.ShouldRecover(CurrentState, out _);

    /// <summary>
    /// Cancel the current state-handler step (NOT the whole engine): the handler
    /// unwinds, the main loop regains control, and its recovery check performs
    /// the actual Dead/Disconnected transition with the run context intact.
    /// Same-thread with the SyncTask pump, so no races. No-op when nothing runs.
    /// </summary>
    public void InterruptForRecovery()
    {
        var step = _stepCts;
        if (step is { IsCancellationRequested: false })
        {
            _log("Recovery interrupt -- cancelling the current action.");
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
            _strategy.Reset();
            CurrentState = DetectInitialState();
            _log($"Engine starting. Strategy: {_strategy.Name}, State: {CurrentState}");

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
                        _stats.Deaths++;
                    await _navigation.ReleaseMoveKey();
                    CurrentState = recoveryState;
                }

                // Loading screens pause the loop -- except while disconnected,
                // where the handler itself must keep driving the login flow.
                if (_gameState.IsLoading && CurrentState != BotState.Disconnected)
                    continue;

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
                        BotState.Idle => DetectInitialState(),
                        BotState.InHideout => DoInHideout(),
                        BotState.Stashing => await DoStashing(stepCt),
                        BotState.PreparingMap => await DoPreparingMap(stepCt),
                        BotState.WaitingForPortal => await DoWaitingForPortal(stepCt),
                        BotState.EnteringMap => await DoEnteringMap(stepCt),
                        BotState.RunningFarm => await DoRunningFarm(stepCt),
                        BotState.ReturningToHideout => await DoReturningToHideout(stepCt),
                        BotState.Dead => await _recovery.HandleDeath(stepCt),
                        BotState.Disconnected => await DoDisconnected(stepCt),
                        BotState.Finished => BotState.Finished,
                        _ => BotState.Error
                    };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Step interrupted for recovery — state unchanged; the next
                    // iteration's ShouldRecover transitions to Dead/Disconnected.
                    await _navigation.ReleaseMoveKey();
                }
                finally
                {
                    _stepCts = null;
                }

                if (CurrentState == BotState.Finished)
                {
                    _log($"Engine finished. Runs completed: {Context.RunsCompleted}.");
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
            await _navigation.ReleaseMoveKey();
        }

        if (ct.IsCancellationRequested)
            _webhook.NotifyCritical("Bot stopped", "Stopped manually.");
        else if (CurrentState == BotState.Finished)
            SendSessionSummary();
        else if (CurrentState == BotState.Error)
            _webhook.NotifyCritical("Bot error", "Engine entered the Error state.");

        _log($"Engine stopped. Final state: {CurrentState}");
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
                _log($"Run still active, {Context.PortalsRemaining} portals left. Re-entering.");
                _portalWaitStartedAt = Clock.NowMs;
                return BotState.WaitingForPortal;
            }

            _log("Run abandoned -- no portals left.");
            _webhook.NotifyCritical("Run abandoned", "No portals left to re-enter -- starting a fresh run.");
            Context.RunActive = false;
        }

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
        _log("Running hideout chores (stash / gems / fragments)...");
        var result = await _chores.Run(_strategy.MapDeviceItemPaths, _strategy.RestockTabName, ct);
        switch (result)
        {
            case ChoresResult.OutOfFragments:
                _log("Fragment input tab is empty -- farming session complete.");
                _finishReason = "Fragment input tab empty.";
                return BotState.Finished;

            case ChoresResult.Done:
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

    private async SyncTask<BotState> DoPreparingMap(CancellationToken ct)
    {
        if (_strategy.MapDeviceItemPaths.Count == 0)
        {
            // Legacy strategies: portals appear without our involvement.
            _portalWaitStartedAt = Clock.NowMs;
            return BotState.WaitingForPortal;
        }

        _log("Preparing map device...");
        var result = await _mapDevice.PrepareAndActivate(
            _strategy.MapDeviceItemPaths, _strategy.IsFarmArea, ct);

        switch (result)
        {
            case MapDeviceResult.Success:
                Context.ResetForNewRun();
                Context.RunActive = true;
                BeginRunTracking();
                _restockRetries = 0;
                _mapDeviceFailuresInARow = 0;
                _mapDeviceTroubleNotified = false;
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

        var portal = portals[0];
        _log($"Entering portal ({portal.GetComponent<ExileCore.PoEMemory.Components.Render>()?.Name ?? "?"})...");
        await _navigation.ClickEntity(portal, ct);

        // Wait for the transition to land in a farm area.
        if (await WaitForAreaTransition(
                () => _gameState.IsReady && _strategy.IsFarmArea(_gameState.AreaName), 12, ct))
        {
            Context.PortalsUsed++;
            // We stand at the return portals right now -- remember the spot.
            Context.MapEntryPoint = _gameState.PlayerGridPos;
            var firstEntry = Context.PortalsUsed == 1;
            _log($"Entered '{_gameState.AreaName}' (portal {Context.PortalsUsed}/{RunContext.MaxPortals}).");
            if (firstEntry)
                _strategy.Reset();
            return BotState.RunningFarm;
        }

        _log("Portal click did not lead into the map, retrying...");
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
            _log("Farm objective complete!");
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
            return BotState.InHideout;
        }

        // Town has no portals of ours -- the hideout keybind is the way home.
        if (_gameState.IsInTown)
        {
            await _recovery.TryTownToHideout(ct);
            await Task.Delay(1000, ct);
            return BotState.ReturningToHideout;
        }

        // In the farm area: take the nearest town portal back.
        var portals = _gameState.GetTownPortals();
        if (portals.Count > 0)
        {
            _log("Taking portal back to hideout...");
            await _navigation.ClickEntity(portals[0], ct);

            if (await WaitForAreaTransition(
                    () => _gameState.IsInHideout || _gameState.IsInTown, 12, ct))
                return BotState.ReturningToHideout; // re-evaluates: hideout exits, town presses the key
        }
        else if (Context.MapEntryPoint is { } entry &&
                 _strategy.IsFarmArea(_gameState.AreaName) &&
                 System.Numerics.Vector2.Distance(_gameState.PlayerGridPos, entry) > 30)
        {
            // The return portals stand at the map entry but stream in only when close.
            _log("Walking back to the map entry point...");
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
