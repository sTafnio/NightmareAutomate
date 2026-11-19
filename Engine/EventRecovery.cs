using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.Shared;
using NightmareAutomate.Enums;
using NightmareAutomate.Helpers;
using NightmareAutomate.Services;

namespace NightmareAutomate.Engine;

/// <summary>
/// Detects and handles death and disconnect events.
/// Can interrupt any active state and drive recovery.
/// </summary>
public sealed class EventRecovery
{
    /// <summary>Dead this long with the resurrect option not working: alert.</summary>
    private const int DeadAlertSeconds = 60;

    /// <summary>Dead this long: nothing left to try — stop the session loudly
    /// instead of clicking at a death screen all night.</summary>
    private const int DeadGiveUpSeconds = 300;

    private readonly GameStateService _gameState;
    private readonly InputService _input;
    private readonly DiscordWebhookService _webhook;
    private readonly Action<string> _log;
    private readonly Func<Keys> _getHideoutKey;
    private readonly Func<int> _getMaxReconnectAttempts;

    public EventRecovery(
        GameStateService gameState,
        InputService input,
        DiscordWebhookService webhook,
        Action<string> log,
        Func<Keys> getHideoutKey,
        Func<int> getMaxReconnectAttempts)
    {
        _gameState = gameState;
        _input = input;
        _webhook = webhook;
        _log = log;
        _getHideoutKey = getHideoutKey;
        _getMaxReconnectAttempts = getMaxReconnectAttempts;
    }

    /// <summary>
    /// Check if recovery is needed. Returns true and sets recoveryState if so.
    /// Deliberately log-free: the plugin's Tick polls this EVERY FRAME (to
    /// interrupt long-running state handlers); the engine logs the actual
    /// transition when it happens.
    /// </summary>
    public bool ShouldRecover(BotState currentState, out BotState recoveryState)
    {
        recoveryState = currentState;

        // Don't interrupt recovery states or idle
        if (currentState is BotState.Dead or BotState.Disconnected or
            BotState.Idle or BotState.Error)
        {
            return false;
        }

        if (_gameState.IsPlayerDead)
        {
            recoveryState = BotState.Dead;
            return true;
        }

        // The login-screen check fires regardless of IsLoading: a disconnect
        // lands on screens where the host's cached IsLoading/InGame values are
        // not contractual, and recovery must never be gated off by them.
        if (_gameState.IsAtLoginScreen ||
            (!_gameState.IsConnected && !_gameState.IsLoading))
        {
            recoveryState = BotState.Disconnected;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Where to send the state machine after a respawn/reconnect landed us
    /// somewhere: hideout is home; town and in-map both route through
    /// ReturningToHideout, which knows the way back from either.
    /// </summary>
    private BotState StateFromCurrentArea()
    {
        if (_gameState.IsInHideout)
            return BotState.InHideout;

        _log(_gameState.IsInTown
            ? "Ended up in town. Returning to hideout..."
            : "Still in a map/area. Returning to hideout for safety.");
        return BotState.ReturningToHideout;
    }

    /// <summary>
    /// In town: press the in-game hideout keybind (a Command Shortcut bound to
    /// /hideout) and wait for the transition to land in the hideout.
    /// </summary>
    public async SyncTask<bool> TryTownToHideout(CancellationToken ct)
    {
        if (!_gameState.IsInTown) return false;

        _log("In town -- pressing the hideout key...");
        await _input.PressKey(_getHideoutKey(), ct);

        // The command queues a short transition; the wait rides out the loading
        // screen (IsInHideout stays false during it) up to the timeout.
        var arrived = await WaitHelpers.For(() => _gameState.IsInHideout, 8000, ct);
        if (!arrived)
            _log("Still in town -- is the Hideout Key bound to /hideout in the game options?");
        return arrived;
    }

    /// <summary>
    /// Handle player death -- click the resurrect button, then return to hideout state.
    /// </summary>
    public async SyncTask<BotState> HandleDeath(CancellationToken ct)
    {
        _log("Handling death... waiting for resurrect.");

        var deathHandlingSince = Clock.NowMs;
        var stuckNotified = false;
        while (!ct.IsCancellationRequested && _gameState.IsPlayerDead)
        {
            var deadFor = Clock.SecondsSince(deathHandlingSince);
            if (deadFor > DeadGiveUpSeconds)
            {
                _log($"Still dead after {DeadGiveUpSeconds / 60} minutes -- giving up.");
                _webhook.NotifyCritical("Stuck dead",
                    $"The character has been dead for {DeadGiveUpSeconds / 60} minutes and no resurrect " +
                    "option worked -- stopping the session.");
                return BotState.Finished;
            }

            if (deadFor > DeadAlertSeconds && !stuckNotified)
            {
                stuckNotified = true;
                _webhook.NotifyCritical("Still dead",
                    $"Dead for over {DeadAlertSeconds}s -- the resurrect button was not found or did not work. " +
                    $"Retrying; the session stops after {DeadGiveUpSeconds / 60} minutes.");
            }

            // Checkpoint resurrection spawns us in the hideout; town does not. Only
            // fall back to any resurrect option if no checkpoint button shows up.
            var allowFallback = deadFor > 15;
            await TryClickResurrect(allowFallback, ct);
            await Task.Delay(500, ct);
        }

        // A cut-short handler is re-run, not a failure (the engine resumes it).
        ct.ThrowIfCancellationRequested();

        _log("Player alive again. Waiting for area to stabilize...");
        await Task.Delay(2000, ct);

        return StateFromCurrentArea();
    }

    /// <summary>
    /// Click the resurrect button on the death panel -- "Resurrect at Checkpoint"
    /// (spawns in the hideout), never "Resurrect in Town" unless the checkpoint
    /// option never appears (allowFallback).
    /// Verified live 2026-11-14: the labels ('resurrect at checkpoint', 'resurrect
    /// in town') are in Element.Text; TextNoTags is empty on them (and on almost
    /// every other UI element in this HUD build).
    /// </summary>
    private async SyncTask<bool> TryClickResurrect(bool allowFallback, CancellationToken ct)
    {
        var panel = _gameState.Controller.IngameState?.IngameUi?.ResurrectPanel;
        if (panel == null || !panel.IsVisible) return false;

        var button = panel.FindChildRecursive(e =>
            e.IsVisible && LabelOf(e).Contains("Checkpoint", StringComparison.OrdinalIgnoreCase), 8, 0);

        if (button == null && allowFallback)
        {
            _log("No checkpoint resurrect option found -- falling back to any resurrect button.");
            button = panel.FindChildRecursive(e =>
                e.IsVisible && LabelOf(e).Contains("Resurrect", StringComparison.OrdinalIgnoreCase), 8, 0);
        }

        if (button == null) return false;

        // Give the death panel a moment to settle before clicking -- clicking the
        // instant it appears can be swallowed or hit a shifting layout.
        await Task.Delay(1000, ct);

        _log($"Clicking resurrect button ('{LabelOf(button)}')...");
        await _input.ClickRect(button.GetClientRectCache, MouseButtons.Left, ct);
        await Task.Delay(1000, ct);
        return true;
    }

    /// <summary>An element's visible label: Text, or TextNoTags if Text is empty.</summary>
    private static string LabelOf(ExileCore.PoEMemory.Element e)
    {
        var text = e.Text;
        return string.IsNullOrEmpty(text) ? e.TextNoTags ?? "" : text;
    }

    /// <summary>
    /// Handle disconnect -- drive the login flow home with Enter, then resume.
    ///
    /// A PoE disconnect lands on the login screen (dialog + remembered login) and
    /// then character select; pressing Enter advances every step of that chain.
    /// The loop runs on the raw game-state machine (IsInGameState / the real
    /// area-loading state), NOT on the host's cached InGame/IsLoading -- those
    /// are not contractual outside a session and must never starve the presses.
    /// Enter goes through raw OS input (no InputHumanizer: it is a bridge into
    /// another plugin, not guaranteed to serve out of game), and only when the
    /// game window has OS focus -- keys would go elsewhere otherwise. Background
    /// input can't land anywhere else, so it goes through the pipe at any time.
    /// </summary>
    public async SyncTask<BotState> HandleDisconnect(CancellationToken ct)
    {
        var maxAttempts = _getMaxReconnectAttempts();
        var whileFocused = _input.IsBackgroundInput ? "" : " while the game window is focused";
        _log($"Handling disconnect... pressing Enter to log back in{whileFocused}.");
        _webhook.NotifyCritical("Disconnected",
            $"Connection lost. Auto-relogin presses Enter{whileFocused} " +
            "(every 5s at first, backing off to 30s). " +
            (maxAttempts > 0
                ? $"Gives up after {maxAttempts} attempts."
                : "Keeps trying until reconnected."));

        var disconnectedAt = Clock.NowMs;
        var lastEnterAt = Clock.Never;
        var attempts = 0;
        var nextReminder = TimeSpan.FromMinutes(10);
        var warnedBackground = false;

        while (!ct.IsCancellationRequested && !_gameState.IsInGameState)
        {
            var elapsed = TimeSpan.FromMilliseconds(Clock.MsSince(disconnectedAt));

            // Periodic reminders: after the first alert, webhook silence must
            // MEAN "back in game", not "still stuck". 10m, 30m, then hourly.
            if (elapsed >= nextReminder)
            {
                nextReminder = nextReminder.TotalMinutes switch
                {
                    < 30 => TimeSpan.FromMinutes(30),
                    < 60 => TimeSpan.FromMinutes(60),
                    _ => nextReminder + TimeSpan.FromMinutes(60)
                };
                var focusNote = _gameState.IsWindowForeground || _input.IsBackgroundInput
                    ? ""
                    : " The game window is NOT focused -- no keys can be pressed until it is.";
                _webhook.NotifyCritical("Still disconnected",
                    $"{(int)elapsed.TotalMinutes} minutes since the disconnect, " +
                    $"{attempts} relogin attempts so far.{focusNote}");
            }

            // A real loading screen (character select -> world) -- sit it out.
            if (_gameState.IsLoadingState)
            {
                await Task.Delay(500, ct);
                continue;
            }

            if (!_gameState.IsWindowForeground && !_input.IsBackgroundInput)
            {
                if (!warnedBackground)
                {
                    warnedBackground = true;
                    _log("Game window is not focused -- waiting (focus it to let the bot re-login).");
                }
                await Task.Delay(1000, ct);
                continue;
            }

            warnedBackground = false;

            // Escalating cadence: a quick kick recovers within seconds; a long
            // outage (realm restart) must not hammer Enter all night.
            var intervalSeconds = elapsed.TotalMinutes switch
            {
                < 2 => 5,
                < 10 => 15,
                _ => 30
            };

            if (Clock.SecondsSince(lastEnterAt) >= intervalSeconds)
            {
                // Only real presses count as attempts -- time spent waiting for
                // focus or on loading screens never burns the budget.
                if (maxAttempts > 0 && attempts >= maxAttempts)
                {
                    _log($"Could not reconnect after {attempts} attempts -- giving up.");
                    _webhook.NotifyCritical("Reconnect given up",
                        $"Still disconnected after {attempts} relogin attempts " +
                        $"({(int)elapsed.TotalMinutes} minutes) -- stopping the session. " +
                        "Log back in and restart the bot manually.");
                    return BotState.Finished;
                }

                attempts++;
                lastEnterAt = Clock.NowMs;
                _log("Still disconnected -- pressing Enter (attempt " +
                     $"{attempts}{(maxAttempts > 0 ? $"/{maxAttempts}" : "")})...");
                await _input.PressKeyOutOfGame(Keys.Enter, ct);
            }

            await Task.Delay(500, ct);
        }

        ct.ThrowIfCancellationRequested();

        _log("Reconnected. Waiting for the game to become ready...");
        _webhook.NotifyCritical("Reconnected", "Back in game -- resuming the loop.");

        // InGameState appears before the world is fully streamed in -- wait for
        // readiness (bounded) plus a short settle before deciding where we are.
        await WaitHelpers.For(() => _gameState.IsReady, 15000, ct);
        await Task.Delay(2000, ct);

        return StateFromCurrentArea();
    }
}
