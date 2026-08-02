using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ExileCore.Shared;
using NightmareAutomate.Enums;
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
    private readonly MapDeviceService _mapDevice;
    private readonly ChoresService _chores;
    private readonly EventRecovery _recovery;
    private readonly DiscordWebhookService _webhook;
    private readonly SessionStats _stats;
    private readonly Services.ExplorationService _exploration;
    private readonly Action<string> _log;
    private readonly Func<int> _getMaxRuns;

    private bool _noReturnPortalNotified;

    private DateTime _portalWaitStartedAt = DateTime.MinValue;

    private int _stashFailuresInARow;
    private DateTime _stashBackoffUntil = DateTime.MinValue;
    private int _stashBackoffLevel;
    private bool _stashTroubleNotified;
    private int _restockRetries;
    private int _mapDeviceFailuresInARow;
    private bool _mapDeviceTroubleNotified;

    // Notification bookkeeping: per-run values are deltas against these
    // snapshots (taken whenever a run starts or is adopted).
    private DateTime _sessionStartedAt = DateTime.Now;
    private int _sessionStartDeaths;
    private DateTime _runStartedAt = DateTime.Now;
    private int _runStartDeaths;
    private int _runStartGems;
    private string _finishReason = "";

    /// <summary>After a run completes, its leftover portals still stand at the map
    /// device -- they must not be re-adopted (entering once to determine "completed"
    /// is enough). Cleared when a fresh map is activated. STATIC on purpose: the
    /// engine is recreated on every bot start, and a restart right after a completed
    /// run must not re-adopt that run's leftovers either.</summary>
    private static bool _ignoreExistingPortals;

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
        NavigationService navigation,
        GameStateService gameState,
        MapDeviceService mapDevice,
        ChoresService chores,
        EventRecovery recovery,
        DiscordWebhookService webhook,
        SessionStats stats,
        Services.ExplorationService exploration,
        Action<string> log,
        Func<int> getMaxRuns)
    {
        _strategy = strategy;
        _navigation = navigation;
        _gameState = gameState;
        _mapDevice = mapDevice;
        _chores = chores;
        _recovery = recovery;
        _webhook = webhook;
        _stats = stats;
        _exploration = exploration;
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
            _sessionStartedAt = DateTime.Now;
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
            _log($"CRITICAL ENGINE FATAL: {ex.Message}");
            _webhook.NotifyCritical("Bot error", $"Engine crashed: {ex.Message}");
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
        _runStartedAt = DateTime.Now;
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
        lines.Add($"Duration: {FormatDuration(DateTime.Now - _runStartedAt)}");
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
        var elapsed = DateTime.Now - _sessionStartedAt;
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

    private BotState DoInHideout()
    {
        // Farm portals already up (e.g. manually activated, or bot restarted mid-run):
        // adopt them instead of consuming a fresh fragment. Used count is inferred
        // from how many of the 6 portals remain. Skipped when they belong to a run
        // we just completed -- those lead back into a finished map.
        if (!Context.RunActive && !_ignoreExistingPortals)
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
                _portalWaitStartedAt = DateTime.Now;
                return BotState.WaitingForPortal;
            }

            _log("Run abandoned -- no portals left.");
            _webhook.NotifyCritical("Run abandoned", "No portals left to re-enter -- starting a fresh run.");
            Context.RunActive = false;
        }

        // A standing tab-settings problem WITH a full inventory: the config latch
        // keeps the stash chores off (retrying can't fix a wrong tab name), but
        // farming on in this state means no loot gets picked — stop loudly
        // instead. (The latch lifts on its own when the settings change.)
        if (_chores.ConfigErrorStanding && _chores.UrgentStashPending)
        {
            var detail = _chores.LastFailureDetail ?? "A configured stash tab was not found.";
            _log($"Stash settings problem with a FULL inventory -- stopping. {detail}");
            _webhook.NotifyCritical("Stash settings problem",
                $"{detail} The inventory is full, so farming on cannot pick loot -- session stopped. " +
                "Fix the tab settings and restart.");
            _finishReason = "Stash settings problem with a full inventory.";
            return BotState.Finished;
        }

        // Chores (stash dump, gem level-ups, fragment restock) that
        // keep failing must not loop the InHideout<->Stashing states forever --
        // back off (escalating) and farm on.
        if (DateTime.Now >= _stashBackoffUntil && _chores.HasPending(_strategy.MapDeviceItemPaths))
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
        var result = await _chores.Run(_strategy.MapDeviceItemPaths, ct);
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
                var detail = _chores.LastFailureDetail ?? "A configured stash tab was not found.";
                if (_chores.UrgentStashPending)
                {
                    _log($"Stash settings problem with a FULL inventory -- stopping. {detail}");
                    _webhook.NotifyCritical("Stash settings problem",
                        $"{detail} The inventory is full, so farming on cannot pick loot -- session stopped. " +
                        "Fix the tab settings and restart.");
                    _finishReason = "Stash settings problem with a full inventory.";
                    return BotState.Finished;
                }

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
                _stashBackoffUntil = DateTime.Now.AddSeconds(backoffSeconds);
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
            _portalWaitStartedAt = DateTime.Now;
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
                _ignoreExistingPortals = false; // fresh portals from here on
                _portalWaitStartedAt = DateTime.Now;
                return BotState.WaitingForPortal;

            case MapDeviceResult.NoFragments:
                // "Nothing on hand" is only final when no stash restock exists to
                // fix it. After a failed/backed-off stash session the fragments
                // usually still sit in the tab -- lift the backoff and give the
                // chores another (bounded) chance instead of ending the session
                // on a false "out of fragments". The true tab-empty signal is
                // ChoresResult.OutOfFragments, handled in DoStashing.
                if (_chores.CanRestock(_strategy.MapDeviceItemPaths))
                {
                    if (_restockRetries < MaxRestockRetries)
                    {
                        _restockRetries++;
                        _stashBackoffUntil = DateTime.MinValue;
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
                        $"Opening a map has failed 3x in a row ({detail}) -- resetting the UI and retrying; " +
                        $"will stop after {MaxMapDeviceFailures} failures.");
                }

                _log($"Map device flow failed ({detail}) -- resetting the UI and retrying shortly.");
                await _mapDevice.ResetUi(ct);
                await Task.Delay(_mapDeviceFailuresInARow >= 3 ? 10000 : 2000, ct);
                return BotState.InHideout;
            }
        }
    }

    private async SyncTask<BotState> DoWaitingForPortal(CancellationToken ct)
    {
        var portals = _gameState.FindFarmPortals(_strategy.IsFarmArea);
        if (portals.Count > 0)
            return BotState.EnteringMap;

        if (_portalWaitStartedAt == DateTime.MinValue)
            _portalWaitStartedAt = DateTime.Now;

        if ((DateTime.Now - _portalWaitStartedAt).TotalSeconds > 15)
        {
            _portalWaitStartedAt = DateTime.MinValue;
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
            _portalWaitStartedAt = DateTime.Now;
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

        bool farmComplete = await _strategy.ExecuteFarm(Context, ct);

        if (farmComplete)
        {
            _log("Farm objective complete!");
            Context.RunActive = false;
            Context.RunsCompleted++;
            _ignoreExistingPortals = true; // leftovers lead into the finished map
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
            var deadline = DateTime.Now.AddMinutes(3);
            await _exploration.ExploreUntil(
                Helpers.WaitHelpers.Throttle(
                    () => _gameState.GetTownPortals().Count > 0 || DateTime.Now > deadline, 250), ct);

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
    /// and returns true as soon as the arrival condition holds.
    /// </summary>
    private async SyncTask<bool> WaitForAreaTransition(
        Func<bool> arrived, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline && !ct.IsCancellationRequested)
        {
            await TaskUtils.NextFrame();
            if (_gameState.IsLoading) continue;
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
