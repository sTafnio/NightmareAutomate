using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared;
using InputHumanizer.Input;
using NightmareAutomate.Helpers;

namespace NightmareAutomate.Services;

/// <summary>
/// Centralized input via InputHumanizer for human-like mouse movement and key presses.
/// </summary>
public sealed class InputService
{
    private readonly GameController _gc;
    private readonly Func<string, IInputController>? _tryGetInputController;
    private readonly string _pluginName;

    public InputService(GameController gc, string pluginName)
    {
        _gc = gc;
        _pluginName = pluginName;
        _tryGetInputController = gc.PluginBridge
            .GetMethod<Func<string, IInputController>>("InputHumanizer.TryGetInputController");
    }

    public bool IsAvailable => _tryGetInputController != null;

    /// <summary>
    /// Acquires an InputHumanizer controller. Caller must dispose when done.
    /// </summary>
    public IInputController? AcquireController() =>
        _tryGetInputController?.Invoke(_pluginName);

    private Vector2 _lastHoverPos;
    private long _lastHoverAt = Clock.Never;

    /// <summary>
    /// Move the mouse to a screen position using humanized movement.
    /// </summary>
    public async SyncTask<bool> MoveMouse(Vector2 screenPos, CancellationToken ct)
    {
        using var controller = AcquireController();
        if (controller == null) return false;

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
        using var controller = AcquireController();
        if (controller == null) return false;

        await controller.Click(button, ct);
        return true;
    }

    /// <summary>
    /// Move to a screen position and click.
    /// </summary>
    public async SyncTask<bool> ClickAt(Vector2 screenPos, MouseButtons button, CancellationToken ct)
    {
        using var controller = AcquireController();
        if (controller == null) return false;

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
        using var controller = AcquireController();
        if (controller == null) return false;

        await controller.KeyDown(Keys.LControlKey, ct);
        try
        {
            await controller.MoveMouse(screenPos, ct);
            await controller.Click(MouseButtons.Left, ct);
        }
        finally
        {
            await controller.KeyUp(Keys.LControlKey, false, ct);
        }
        return true;
    }

    /// <summary>
    /// Press and release a key (e.g. toggle the inventory panel, Escape).
    /// </summary>
    public async SyncTask<bool> PressKey(Keys key, CancellationToken ct)
    {
        using var controller = AcquireController();
        if (controller == null) return false;

        await controller.KeyDown(key, ct);
        await controller.KeyUp(key, false, ct);
        return true;
    }

    /// <summary>
    /// Press and release a key through raw OS input (ExileCore's Input), no
    /// InputHumanizer. For the login screen only: the humanizer is a bridge into
    /// another plugin and is not guaranteed to serve while out of game, and a
    /// silent null controller here would starve the relogin loop. In-game input
    /// should keep using PressKey.
    /// </summary>
    public async SyncTask<bool> PressKeyRaw(Keys key, CancellationToken ct)
    {
        Input.KeyDown(key);
        await Task.Delay(Random.Shared.Next(25, 65), ct);
        Input.KeyUp(key);
        return true;
    }

    /// <summary>
    /// Press and hold a key down.
    /// </summary>
    public async SyncTask<bool> KeyDown(Keys key, CancellationToken ct)
    {
        using var controller = AcquireController();
        if (controller == null) return false;

        await controller.KeyDown(key, ct);
        return true;
    }

    /// <summary>
    /// Release a held key.
    /// </summary>
    public async SyncTask<bool> KeyUp(Keys key)
    {
        using var controller = AcquireController();
        if (controller == null) return false;

        await controller.KeyUp(key, false, default);
        return true;
    }

    /// <summary>
    /// Check if a key is currently held down.
    /// </summary>
    public static bool IsKeyDown(Keys key) => Input.IsKeyDown(key);
}
