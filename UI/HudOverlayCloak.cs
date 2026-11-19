using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NightmareAutomate.UI;

/// <summary>
/// Hides the HUD's overlay window while another window is in front. "Force
/// Foreground" keeps plugins running while the game is unfocused, which the bot
/// needs, but the overlay then draws over whatever is on top of the game. Cloaking
/// only takes the window off the screen: the HUD and its plugins carry on as usual.
/// </summary>
public sealed class HudOverlayCloak
{
    private const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private IntPtr _overlay;

    // Unknown at first: a plugin reload may have left the window cloaked.
    private bool? _cloaked;

    public void Update(bool gameInFront, bool enabled)
    {
        if (_overlay == IntPtr.Zero)
        {
            using var hud = Process.GetCurrentProcess();
            _overlay = hud.MainWindowHandle;
            if (_overlay == IntPtr.Zero) return;
        }

        // The overlay itself comes to the front while its menu is being clicked.
        Set(enabled && !gameInFront && GetForegroundWindow() != _overlay);
    }

    public void Show()
    {
        if (_overlay != IntPtr.Zero)
            Set(false);
    }

    private void Set(bool cloak)
    {
        if (_cloaked == cloak) return;
        _cloaked = cloak;
        var value = cloak ? 1 : 0;
        DwmSetWindowAttribute(_overlay, DwmwaCloak, ref value, sizeof(int));
    }
}
