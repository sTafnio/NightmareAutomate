using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared;
using NightmareAutomate.Engine;
using NightmareAutomate.Enums;
using NightmareAutomate.Services;
using NightmareAutomate.Strategies;
using NightmareAutomate.UI;

namespace NightmareAutomate;

public class NightmareAutomate : BaseSettingsPlugin<NightmareAutomateSettings>
{
    private CancellationTokenSource? _cts;
    private SyncTask<bool>? _currentTask;

    // Services
    private GameStateService _gameState = null!;
    private GridPathfinder _pathfinding = null!;
    private InputService _input = null!;
    private InventoryService _inventory = null!;
    private NavigationService _navigation = null!;
    private MapDeviceService _mapDevice = null!;
    private LootService _lootService = null!;
    private StashService _stashService = null!;

    private ExplorationService _exploration = null!;
    private GemLevelService _gems = null!;
    private ChoresService _chores = null!;
    private DiscordWebhookService _webhook = null!;
    private Models.SessionStats _stats = null!;

    // Engine
    private FarmingEngine? _engine;
    private EventRecovery _recovery = null!;

    // UI
    private DebugOverlay _overlay = null!;

    public override bool Initialise()
    {
        Name = "Nightmare Automate";

        // Keep ticking while OUT of game: ExileCore skips plugins entirely when
        // GameController.InGame is false unless Force is set. Without it the
        // SyncTask pump freezes on disconnect and login recovery can never run.
        Force = true;

        // UI overlay (created first so services can log to it)
        _overlay = new DebugOverlay();

        // Create services
        _gameState = new GameStateService(GameController);
        _pathfinding = new GridPathfinder(GameController);
        _input = new InputService(GameController, Name);
        _inventory = new InventoryService(GameController);

        _navigation = new NavigationService(
            _gameState,
            _pathfinding,
            _input,
            msg => _overlay.Log(msg),
            () => Settings.PathLookahead,
            () => Settings.MoveKey.Value.Key);

        _lootService = new LootService(
            _gameState,
            _inventory,
            _navigation,
            _input,
            msg => _overlay.Log(msg),
            () => Settings.ToggleLabelsKey.Value.Key);

        _mapDevice = new MapDeviceService(
            _gameState,
            _inventory,
            _navigation,
            _input,
            msg => _overlay.Log(msg),
            () => Settings.InventoryKey.Value.Key);

        _stashService = new StashService(
            _gameState,
            _inventory,
            _navigation,
            _input,
            msg => _overlay.Log(msg),
            () => Settings.DumpTabNames);

        // Must exist BEFORE EventRecovery below -- the constructor captures the
        // field VALUE, so a later assignment would leave recovery with null.
        _webhook = new DiscordWebhookService(Settings, msg => _overlay.Log(msg));
        Settings.SendTestNotification = () => _webhook.SendTest();

        _recovery = new EventRecovery(
            _gameState,
            _input,
            _webhook,
            msg => _overlay.Log(msg),
            () => Settings.HideoutKey.Value.Key);

        _exploration = new ExplorationService(_gameState, _navigation, msg => _overlay.Log(msg));

        _stats = new Models.SessionStats();

        _gems = new GemLevelService(_gameState, _input, _stats, msg => _overlay.Log(msg));

        _chores = new ChoresService(
            _stashService,
            _gems,
            _inventory,
            Settings,
            msg => _overlay.Log(msg));

        // The dump-tab picker in the settings UI needs live stash tab names.
        Settings.StashTabNames = () => _gameState.GetStashTabNames();

        // Register hotkeys
        Input.RegisterKey(Settings.ToggleButton.Value.Key);
        Settings.ToggleButton.OnValueChanged += () => Input.RegisterKey(Settings.ToggleButton.Value.Key);

        // Disabling the plugin mid-run freezes the task (Tick stops pumping it):
        // cancel and force-release everything the bot might be holding. If the
        // plugin is re-enabled, the task unwinds cleanly through its finally
        // blocks on the next ticks.
        Settings.Enable.OnValueChanged += (_, enabled) =>
        {
            if (!enabled && _currentTask != null)
            {
                _cts?.Cancel();
                ForceReleaseHeldKeys();
            }
        };

        // Log service availability
        if (!_input.IsAvailable)
            LogError("InputHumanizer bridge is not available.");

        return true;
    }

    public override Job Tick()
    {
        // Toggle on/off
        if (Settings.ToggleButton.UnpressedOnce())
        {
            if (_currentTask != null)
                RequestStop();
            else
                Start();
        }

        // Recovery watchdog: state handlers await INSIDE the engine's SyncTask,
        // so the engine loop cannot see a death/disconnect that happens
        // mid-action — but Tick keeps running regardless. Detect it here and
        // cancel the current handler step; the engine's own recovery then
        // transitions with the run context intact.
        if (_currentTask != null && _engine != null && _engine.NeedsRecoveryInterrupt)
            _engine.InterruptForRecovery();

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
        var playerPos = _gameState.PlayerGridPos;

        List<(string, string)>? extraRows = null;
        if (_engine != null)
        {
            var ctx = _engine.Context;
            extraRows = [];
            foreach (var row in _engine.Strategy.GetStatusRows(ctx))
                extraRows.Add(row);
            extraRows.Add(("Portals", $"{ctx.PortalsUsed}/{Models.RunContext.MaxPortals}"));
            extraRows.Add(("Runs", $"{ctx.RunsCompleted}"));
        }

        _overlay.Draw(state, strategyName, phaseName, playerPos, extraRows);
    }

    public override void OnClose()
    {
        // HUD is shutting down -- nothing will pump the task anymore.
        _cts?.Cancel();
        if (_currentTask != null)
            ForceReleaseHeldKeys();
        _webhook?.Dispose();
    }

    private void Start()
    {
        _overlay.Log("Attempting to start bot...");
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
                _navigation,
                _gameState,
                _mapDevice,
                _chores,
                _recovery,
                _webhook,
                _stats,
                _overlay.Log,
                () => Settings.MaxRuns);

            _cts = new CancellationTokenSource();
            _currentTask = _engine.Run(_cts.Token);

            _overlay.Log($"Bot engine started: {strategy.Name}");
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

        _overlay.Log("Stop requested -- unwinding...");
        _cts.Cancel();
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
        _overlay.Log("Bot stopped.");
    }

    /// <summary>
    /// Raw key-up for everything the bot could be holding down -- used when the
    /// task cannot (or might not) unwind on its own.
    /// </summary>
    private void ForceReleaseHeldKeys()
    {
        Input.KeyUp(Settings.MoveKey.Value.Key);
        Input.KeyUp(Keys.LControlKey);
        Input.KeyUp(Keys.LShiftKey);
    }

    private IFarmStrategy? CreateStrategy()
    {
        return Settings.FarmType switch
        {
            FarmType.Simulacrum => new SimulacrumStrategy(
                _navigation, _gameState, _lootService, _inventory, _stashService,
                _exploration, _input, _gems, Settings, _overlay.Log),

            _ => null
        };
    }
}
