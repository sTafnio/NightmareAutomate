using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared;
using NightmareAutomate.Engine;
using NightmareAutomate.Enums;
using NightmareAutomate.Helpers;
using NightmareAutomate.Services;
using NightmareAutomate.Strategies;
using NightmareAutomate.UI;

namespace NightmareAutomate;

public class NightmareAutomate : BaseSettingsPlugin<NightmareAutomateSettings>
{
    private CancellationTokenSource? _cts;
    private SyncTask<bool>? _currentTask;

    /// <summary>Everything the engine and strategies consume, built once in
    /// Initialise. Individual services the plugin itself touches are reached
    /// through this bundle too.</summary>
    private BotServices _services = null!;

    // Engine
    private FarmingEngine? _engine;

    // UI
    private DebugOverlay _overlay = null!;

    private readonly HudOverlayCloak _hudOverlay = new();

    /// <summary>
    /// Standard log channel for outcomes and problems: the on-screen overlay AND
    /// ExileCore's file log (Logs\Info*.log via BasePlugin.LogMessage, which adds
    /// the plugin name) — so a failed overnight session leaves a trail instead of 4
    /// overlay lines. Routine progress goes through BotLog.Step, and per-frame or
    /// per-second chatter (movement steering) directly to the overlay.
    /// </summary>
    private void LogInfo(string msg)
    {
        _overlay.Log(msg);
        LogMessage(msg);
    }

    public override bool Initialise()
    {
        Name = "Nightmare Automate";

        // Keep ticking while OUT of game: ExileCore skips plugins entirely when
        // GameController.InGame is false unless Force is set. Without it the
        // SyncTask pump freezes on disconnect and login recovery can never run.
        Force = true;

        // Counted diagnostics for failed game-memory reads (file log only —
        // routine hiccups must not spam the overlay).
        SafeRead.Log = LogMessage;

        // UI overlay (created first so services can log to it)
        _overlay = new DebugOverlay();
        BotLog.StepLog = msg =>
        {
            _overlay.Log(msg);
            if (Settings.VerboseLogs)
                LogMessage(msg);
        };

        // Create services
        var areas = new AreaMemory();
        var gameState = new GameStateService(GameController);
        var pathfinding = new GridPathfinder(GameController);
        var input = new InputService(GameController, Name, LogInfo, () => Settings.UseBackgroundInput);
        var inventory = new InventoryService(GameController, () => Settings.KeepOmenOfAmelioration);

        var navigation = new NavigationService(
            gameState,
            pathfinding,
            input,
            LogInfo,
            msg => _overlay.Log(msg), // steering chatter: overlay only, never the file
            () => Settings.PathLookahead,
            () => Settings.MoveKey.Value.Key);

        var loot = new LootService(
            gameState,
            inventory,
            navigation,
            input,
            LogInfo,
            () => Settings.ToggleLabelsKey.Value.Key);

        var exploration = new ExplorationService(gameState, navigation, LogInfo);

        var mapDevice = new MapDeviceService(
            gameState,
            inventory,
            navigation,
            input,
            exploration,
            areas,
            LogInfo,
            () => Settings.InventoryKey.Value.Key);

        var stash = new StashService(
            gameState,
            inventory,
            navigation,
            input,
            LogInfo,
            () => Settings.DumpTabNames);

        var webhook = new DiscordWebhookService(Settings, LogInfo, () => gameState.PlayerIdentity);
        Settings.SendTestNotification = () => webhook.SendTest();
        Settings.DumpAtlasUi = () => mapDevice.DumpAtlasUiTree("manual dump from settings");

        var recovery = new EventRecovery(
            gameState,
            input,
            webhook,
            LogInfo,
            () => Settings.HideoutKey.Value.Key,
            () => Settings.MaxReconnectAttempts);

        var stats = new Models.SessionStats();

        var gems = new GemLevelService(gameState, input, stats, LogInfo);

        var chores = new ChoresService(
            stash,
            gems,
            inventory,
            Settings,
            LogInfo);

        _services = new BotServices(
            gameState, input, inventory, navigation, mapDevice, loot, stash,
            exploration, gems, chores, recovery, webhook, stats, areas);

        // The dump-tab picker in the settings UI needs live stash tab names.
        Settings.StashTabNames = () => gameState.GetStashTabNames();
        Settings.BackgroundInputMismatch = () => input.BackgroundInputMismatch;
        Settings.StopAfterRunPending = () => _currentTask != null ? _engine?.StopAfterRun : null;
        Settings.ToggleStopAfterRun = ToggleStopAfterRun;

        // Register hotkeys
        Input.RegisterKey(Settings.ToggleButton.Value.Key);
        Settings.ToggleButton.OnValueChanged += () => Input.RegisterKey(Settings.ToggleButton.Value.Key);

        // Disabling the plugin mid-run freezes the task (Tick stops pumping it):
        // cancel and force-release everything the bot might be holding --
        // including InputHumanizer's lock, which would otherwise stay taken
        // until re-enable and block every other plugin's input. If the plugin is
        // re-enabled, the task unwinds cleanly through its finally blocks on the
        // next ticks.
        Settings.Enable.OnValueChanged += (_, enabled) =>
        {
            if (!enabled && _currentTask != null)
            {
                _cts?.Cancel();
                ForceReleaseHeldKeys();
                _services.Input.EndSession();
            }

            // Tick stops with the plugin, so nothing would bring the overlay back.
            if (!enabled)
                _hudOverlay.Show();
        };

        // Log service availability. Not an error yet: the bridge is looked up
        // lazily and InputHumanizer may simply load after this plugin -- the
        // engine re-checks (and refuses to start without it) on Start.
        if (!input.IsAvailable)
            LogMessage("InputHumanizer bridge not registered yet -- re-checked when the bot starts.");

        return true;
    }

    public override void AreaChange(AreaInstance area) => _services?.GameState.NoteAreaChange();

    public override Job Tick()
    {
        _services.GameState.TrackAreaChange();
        _services.GameState.TrackPlayerIdentity();
        _hudOverlay.Update(_services.GameState.IsWindowForeground, Settings.HideHudWhileUnfocused);

        // Toggle on/off. Only from this game's own window: the hotkey is read from the whole desktop, so with
        // several games running it would otherwise toggle every bot at once (and End is a common typing key).
        // Called first so the key's press state keeps tracking while another window is in front.
        if (Settings.ToggleButton.UnpressedOnce() && _services.GameState.IsWindowForeground)
        {
            if (_currentTask != null)
                RequestStop();
            else
                Start();
        }

        // Recovery / focus watchdog: state handlers await INSIDE the engine's
        // SyncTask, so the engine loop cannot see a death/disconnect — or the
        // game window losing focus — that happens mid-action, but Tick keeps
        // running regardless. Detect it here and cancel the current handler
        // step; the engine's recovery / focus gate then takes over with the run
        // context intact.
        if (_currentTask != null && _engine != null)
        {
            if (_engine.NeedsRecoveryInterrupt)
                _engine.InterruptStep("Recovery interrupt");
            else if (_engine.NeedsFocusInterrupt)
                _engine.InterruptStep("Game window lost focus");
        }

        // Pump the current task. On stop, cancellation unwinds it over the next
        // few frames -- the pump keeps running so every finally/using block
        // (move key release, InputHumanizer controller disposal) executes.
        if (_currentTask != null)
        {
            TaskUtils.RunOrRestart(ref _currentTask, () => null);
            if (_currentTask == null)
                OnRunEnded();
        }

        return null!;
    }

    public override void Render()
    {
        if (!Settings.DebugOverlay) return;

        var state = _engine?.CurrentState ?? BotState.Idle;
        var strategyName = _engine?.Strategy.Name ?? "-";
        var phaseName = _engine?.Strategy.CurrentPhaseName ?? "-";
        var playerPos = _services.GameState.PlayerGridPos;

        List<(string, string)>? extraRows = null;
        if (_engine != null)
        {
            var ctx = _engine.Context;
            extraRows = [];
            foreach (var row in _engine.Strategy.GetStatusRows(ctx))
                extraRows.Add(row);
            extraRows.Add(("Portals", $"{ctx.PortalsUsed}/{Models.RunContext.MaxPortals}"));
            extraRows.Add(("Runs", $"{ctx.RunsCompleted}"));
            if (_currentTask != null && _engine.StopAfterRun)
                extraRows.Add(("Stopping", "after this run"));
        }

        _overlay.Draw(state, strategyName, phaseName, playerPos, extraRows);
    }

    public override void OnClose()
    {
        // HUD is shutting down -- nothing will pump the task anymore.
        _cts?.Cancel();
        if (_currentTask != null)
            ForceReleaseHeldKeys();
        _services?.Input.EndSession();
        _services?.Webhook.Dispose();
        _hudOverlay.Show();
    }

    private void Start()
    {
        try
        {
            var strategy = CreateStrategy();
            if (strategy == null)
            {
                var error = $"Unknown farm type: {Settings.FarmType}";
                LogError(error);
                _overlay.Log(error);
                return;
            }

            _engine = new FarmingEngine(
                strategy,
                _services,
                LogInfo,
                () => Settings.MaxRuns);

            _cts = new CancellationTokenSource();
            _currentTask = _engine.Run(_cts.Token);

            if (_services.Input.BackgroundInputMismatch)
            {
                const string warning = "'Use Background Input' is on here but off in InputHumanizer -- " +
                                       "the bot will pause whenever the game isn't focused.";
                LogError(warning);
                _overlay.Log(warning);
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"CRIT: Start failed: {ex.Message}";
            LogError($"{errorMsg}\n{ex.StackTrace}");
            _overlay.Log(errorMsg);
        }
    }

    /// <summary>
    /// Cooperative stop: cancel and let Tick keep pumping the task to completion,
    /// so finally/using blocks run (keys released, InputHumanizer controller
    /// disposed). Abandoning the task mid-await would leak both.
    /// </summary>
    private void RequestStop()
    {
        if (_cts is not { IsCancellationRequested: false }) return;

        LogInfo("Stop requested.");
        _cts.Cancel();
    }

    /// <summary>
    /// Settings-UI button: let the current run finish, then stop in the hideout
    /// (the engine decides where -- see FarmingEngine.StopAfterRun). Pressing it
    /// again cancels. No-op while the bot is not running.
    /// </summary>
    private void ToggleStopAfterRun()
    {
        if (_currentTask == null || _engine == null) return;

        _engine.StopAfterRun = !_engine.StopAfterRun;
        LogInfo(_engine.StopAfterRun
            ? "Stop after this run: armed -- the bot stops in the hideout once the current run is done."
            : "Stop after this run: cancelled -- farming continues.");
    }

    /// <summary>
    /// The task finished (stop completed, run limit reached, or fatal error).
    /// </summary>
    private void OnRunEnded()
    {
        _cts?.Dispose();
        _cts = null;

        // Safety net: the cooperative unwind should have released these already.
        ForceReleaseHeldKeys();
        _services.Input.EndSession();
    }

    /// <summary>
    /// Raw key-up for everything the bot could be holding down -- used when the
    /// task cannot (or might not) unwind on its own.
    /// </summary>
    private void ForceReleaseHeldKeys()
    {
        // Background input holds its keys inside the game and releases them when the
        // session ends; raw key-ups would only reach the window in front.
        if (_services.Input.IsBackgroundInput)
            return;

        Input.KeyUp(Settings.MoveKey.Value.Key);
        Input.KeyUp(Keys.LControlKey);
        Input.KeyUp(Keys.LShiftKey);
    }

    private IFarmStrategy? CreateStrategy()
    {
        return Settings.FarmType switch
        {
            FarmType.Simulacrum => new SimulacrumStrategy(_services, Settings, LogInfo),

            _ => null
        };
    }
}
