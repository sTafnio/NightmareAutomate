using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared;
using ExileCore.Shared.Nodes;
using InputHumanizer.Input;
using NightmareAutomate.Helpers;

namespace NightmareAutomate.Services;

/// <summary>
/// Centralized input via InputHumanizer for human-like mouse movement and key presses.
///
/// Ownership: while the bot runs it holds InputHumanizer's lock for the WHOLE
/// session (<see cref="BeginSession"/>) — the bot is the only input driver, no
/// other humanizer client can slip a cursor move between a hover and its click,
/// and per-key hold timing carries across calls. Outside a session each action
/// takes the lock for itself (<see cref="Rent"/>).
///
/// Releases never fail: a key-up without a controller goes through raw OS input,
/// and every release ignores cancellation — a cancelled step must not leave a
/// key held down in the game.
/// </summary>
public sealed class InputService
{
    private readonly GameController _gc;
    private readonly string _pluginName;
    private readonly Action<string> _log;
    private readonly Func<bool> _wantsBackgroundInput;

    // Bridge lookup is lazy: InputHumanizer may load after this plugin, and a
    // lookup cached as null in Initialise would disable input for the whole HUD run.
    private Func<string, IInputController>? _tryGetInputController;

    /// <summary>The controller held for the running bot session (null outside one).</summary>
    private IInputController? _session;

    /// <summary>
    /// Keys pressed during the session and not released yet. Background input holds
    /// keys inside the game, where no OS key state can see them, so
    /// <see cref="IsKeyDown"/> can't rely on the OS alone.
    /// </summary>
    private readonly HashSet<Keys> _held = new();

    private long _unavailableLoggedAt = Clock.Never;

    public InputService(GameController gc, string pluginName, Action<string> log, Func<bool> wantsBackgroundInput)
    {
        _gc = gc;
        _pluginName = pluginName;
        _log = log;
        _wantsBackgroundInput = wantsBackgroundInput;
    }

    private Func<string, IInputController>? TryGetInputController =>
        _tryGetInputController ??= _gc.PluginBridge
            .GetMethod<Func<string, IInputController>>("InputHumanizer.TryGetInputController");

    public bool IsAvailable => TryGetInputController != null;

    /// <summary>
    /// Whether input goes straight into the game through InputHumanizer's pipe
    /// instead of through the OS. It then reaches the game with its window behind
    /// others, and none of it can land in the window that is in front. Needs both
    /// settings: our checkbox alone would still send OS input.
    /// </summary>
    public bool IsBackgroundInput => _wantsBackgroundInput() && HumanizerUsesBackgroundInput;

    /// <summary>Our checkbox is on, but input would still go through the OS.</summary>
    public bool BackgroundInputMismatch => _wantsBackgroundInput() && !HumanizerUsesBackgroundInput;

    private bool _humanizerBackground;
    private long _humanizerCheckedAt = Clock.Never;

    /// <summary>
    /// InputHumanizer's own setting, read from the live plugin so toggling it takes
    /// effect without a reload. At most once a second: this is polled every frame.
    /// Off when it can't be read, which keeps the focus gate on.
    /// </summary>
    private bool HumanizerUsesBackgroundInput
    {
        get
        {
            if (Clock.MsSince(_humanizerCheckedAt) >= 1000)
            {
                _humanizerCheckedAt = Clock.NowMs;
                _humanizerBackground = SafeRead.Try("input.humanizer-background", () =>
                    Core.Current.pluginManager.Plugins
                        .FirstOrDefault(p => p.Name == "InputHumanizer")?.Plugin?._Settings is { } settings &&
                    settings.GetType().GetProperty("UseBackgroundInput")?.GetValue(settings) is ToggleNode { Value: true });
            }
            return _humanizerBackground;
        }
    }

    /// <summary>
    /// Take InputHumanizer's lock for the whole bot session, waiting up to
    /// <paramref name="wait"/> for another plugin to let go of it. False when
    /// InputHumanizer is missing or the lock never came free.
    ///
    /// Polled rather than InputHumanizer's own timed wait: that one takes no
    /// token, so a stop or plugin disable mid-wait would leave a waiter that is
    /// handed the lock later and — never pumped again — never releases it.
    /// </summary>
    public async SyncTask<bool> BeginSession(TimeSpan wait, CancellationToken ct)
    {
        if (_session != null) return true;

        var tryGetInputController = TryGetInputController;
        if (tryGetInputController == null) return false;

        var deadline = Clock.InMs(wait.TotalMilliseconds);
        while (true)
        {
            _session = tryGetInputController(_pluginName);
            if (_session != null) return true;
            if (Clock.Reached(deadline)) return false;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>
    /// Give the session lock back. Idempotent — InputHumanizer's Dispose releases
    /// its semaphore on EVERY call, so a second dispose would let two plugins hold
    /// the "exclusive" lock at once. The field is cleared before disposing.
    /// </summary>
    public void EndSession()
    {
        var session = _session;
        _session = null;
        // Handing control back makes background input release everything it holds.
        _held.Clear();
        session?.Dispose();
    }

    /// <summary>
    /// A controller for one action: the session's when one is held (never
    /// disposed by the action), else a fresh acquisition the lease gives back.
    /// </summary>
    public InputLease Rent()
    {
        if (_session != null)
            return new InputLease(_session, owned: false);

        var controller = TryGetInputController?.Invoke(_pluginName);
        if (controller == null)
            ReportUnavailable();
        return new InputLease(controller, owned: true);
    }

    /// <summary>Throttled: callers retry per frame, one line per 5 s is enough.</summary>
    private void ReportUnavailable()
    {
        if (Clock.SecondsSince(_unavailableLoggedAt) < 5) return;
        _unavailableLoggedAt = Clock.NowMs;
        _log(IsAvailable
            ? "[Input] Another plugin holds the InputHumanizer lock -- input skipped."
            : "[Input] InputHumanizer is not available -- input skipped.");
    }

    private Vector2 _lastHoverPos;
    private long _lastHoverAt = Clock.Never;

    /// <summary>
    /// Move the mouse to a screen position using humanized movement.
    /// </summary>
    public async SyncTask<bool> MoveMouse(Vector2 screenPos, CancellationToken ct)
    {
        using var lease = Rent();
        if (lease.Controller is not { } controller) return false;

        await controller.MoveMouse(screenPos, ct);
        return true;
    }

    /// <summary>
    /// Throttled mouse move for continuous use (movement steering, monster hovering).
    /// Issuing a fresh humanized move every frame makes the cursor dart around —
    /// this skips redundant commands so each movement can play out smoothly.
    /// </summary>
    public async SyncTask<bool> HoverMouse(Vector2 screenPos, CancellationToken ct)
    {
        var sinceMs = Clock.MsSince(_lastHoverAt);
        if (sinceMs < 80)
            return true;
        if (sinceMs < 250 && Vector2.Distance(screenPos, _lastHoverPos) < 25)
            return true;

        _lastHoverAt = Clock.NowMs;
        _lastHoverPos = screenPos;
        return await MoveMouse(screenPos, ct);
    }

    /// <summary>
    /// Click at the current mouse position.
    /// </summary>
    public async SyncTask<bool> Click(MouseButtons button, CancellationToken ct)
    {
        using var lease = Rent();
        if (lease.Controller is not { } controller) return false;

        await controller.Click(button, ct);
        return true;
    }

    /// <summary>
    /// Move to a screen position and click.
    /// </summary>
    public async SyncTask<bool> ClickAt(Vector2 screenPos, MouseButtons button, CancellationToken ct)
    {
        using var lease = Rent();
        if (lease.Controller is not { } controller) return false;

        await controller.MoveMouse(screenPos, ct);
        await controller.Click(button, ct);
        return true;
    }

    /// <summary>
    /// A window-relative UI rect turned into an absolute screen click point,
    /// randomized inside the rect (clicks on exact centers are a bot tell).
    /// </summary>
    public Vector2 RandomScreenPoint(SharpDX.RectangleF windowRelativeRect)
    {
        var point = CoordinateHelpers.RandomPointInRect(windowRelativeRect);
        var window = _gc.Window.GetWindowRectangle();
        return new Vector2(point.X + window.Left, point.Y + window.Top);
    }

    /// <summary>
    /// Click a window-relative UI rect (element, button, item, label) at a
    /// randomized inner point.
    /// </summary>
    public async SyncTask<bool> ClickRect(SharpDX.RectangleF rect, MouseButtons button, CancellationToken ct) =>
        await ClickAt(RandomScreenPoint(rect), button, ct);

    /// <summary>
    /// Ctrl-click a window-relative UI rect (item quick-move) at a randomized
    /// inner point.
    /// </summary>
    public async SyncTask<bool> CtrlClickRect(SharpDX.RectangleF rect, CancellationToken ct) =>
        await CtrlClickAt(RandomScreenPoint(rect), ct);

    /// <summary>
    /// Move to a screen position and ctrl-click it (item quick-move).
    /// The whole sequence runs under one controller acquisition.
    /// </summary>
    public async SyncTask<bool> CtrlClickAt(Vector2 screenPos, CancellationToken ct)
    {
        using var lease = Rent();
        if (lease.Controller is not { } controller) return false;

        await controller.KeyDown(Keys.LControlKey, ct);
        try
        {
            await controller.MoveMouse(screenPos, ct);
            await controller.Click(MouseButtons.Left, ct);
        }
        finally
        {
            await controller.KeyUp(Keys.LControlKey, false, CancellationToken.None);
        }
        return true;
    }

    /// <summary>
    /// Press and release a key (e.g. toggle the inventory panel, Escape).
    /// </summary>
    public async SyncTask<bool> PressKey(Keys key, CancellationToken ct)
    {
        using var lease = Rent();
        if (lease.Controller is not { } controller) return false;

        // KeyDown's only cancellable wait comes BEFORE the press, so once it
        // returns the key is down and the release below must not be cancellable.
        await controller.KeyDown(key, ct);
        await controller.KeyUp(key, false, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Press and release a key for the login screen. Normally raw OS input (ExileCore's
    /// Input), no InputHumanizer: the humanizer is a bridge into another plugin and is
    /// not guaranteed to serve while out of game, and a silent null controller here
    /// would starve the relogin loop. With background input raw keys would land in
    /// the window in front, so they go through the pipe. In-game input should keep
    /// using PressKey.
    /// </summary>
    public async SyncTask<bool> PressKeyOutOfGame(Keys key, CancellationToken ct)
    {
        if (IsBackgroundInput)
            return await PressKey(key, ct);

        Input.KeyDown(key);
        try
        {
            await Task.Delay(Random.Shared.Next(25, 65), ct);
        }
        finally
        {
            Input.KeyUp(key);
        }
        return true;
    }

    /// <summary>
    /// Press and hold a key down.
    /// </summary>
    public async SyncTask<bool> KeyDown(Keys key, CancellationToken ct)
    {
        using var lease = Rent();
        if (lease.Controller is not { } controller) return false;

        await controller.KeyDown(key, ct);
        // Outside a session the lease hands control back at once, which releases it.
        if (_session != null)
            _held.Add(key);
        return true;
    }

    /// <summary>
    /// Release a held key. Always releases: without a controller (another plugin
    /// holds the lock) it falls back to raw OS input — a held move key would keep
    /// the character running. Background input skips the fallback: a raw key-up
    /// would only reach the window in front, and handing control back releases
    /// the key in the game.
    /// </summary>
    public async SyncTask<bool> KeyUp(Keys key)
    {
        using var lease = Rent();
        if (lease.Controller is { } controller)
            await controller.KeyUp(key, false, CancellationToken.None);
        else if (!IsBackgroundInput)
            Input.KeyUp(key);
        _held.Remove(key);
        return true;
    }

    /// <summary>
    /// Release every key the bot ever holds (move key, Ctrl, Shift) that is
    /// currently down. For interrupts and pauses, where the step that pressed
    /// them never reached its own release.
    /// </summary>
    public async SyncTask<bool> ReleaseHeldKeys(Keys moveKey)
    {
        foreach (var key in new[] { moveKey, Keys.LControlKey, Keys.LShiftKey })
        {
            if (IsKeyDown(key))
                await KeyUp(key);
        }
        return true;
    }

    /// <summary>
    /// Check if a key is currently held down, by the bot or on the real keyboard.
    /// With background input the real keyboard doesn't reach the game, so what the
    /// user types elsewhere must not count.
    /// </summary>
    public bool IsKeyDown(Keys key) =>
        _held.Contains(key) || (!IsBackgroundInput && Input.IsKeyDown(key));
}

/// <summary>
/// One action's use of an input controller. Disposes the controller only when
/// the lease acquired it itself — the session controller outlives every action.
/// </summary>
public readonly struct InputLease : IDisposable
{
    private readonly bool _owned;

    public InputLease(IInputController? controller, bool owned)
    {
        Controller = controller;
        _owned = owned;
    }

    public IInputController? Controller { get; }

    public void Dispose()
    {
        if (_owned)
            Controller?.Dispose();
    }
}
