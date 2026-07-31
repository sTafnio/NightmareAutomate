using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using NightmareAutomate.Enums;

namespace NightmareAutomate.UI;

/// <summary>
/// Draws a semi-transparent, non-interactive ImGui overlay at the top-center of the screen.
/// Replaces log-spam with a clean, always-visible status panel.
/// </summary>
public sealed class DebugOverlay
{
    private const int MaxLogLines = 4;
    private const float WindowWidth = 420f;

    private readonly Queue<LogEntry> _logBuffer = new();
    private readonly object _lock = new();

    /// <summary>
    /// Push a message into the overlay log buffer (thread-safe).
    /// </summary>
    public void Log(string message)
    {
        lock (_lock)
        {
            _logBuffer.Enqueue(new LogEntry(DateTime.Now, message));
            while (_logBuffer.Count > MaxLogLines)
                _logBuffer.Dequeue();
        }
    }

    /// <summary>
    /// Call from the plugin's Render() method.
    /// </summary>
    public void Draw(
        BotState state,
        string strategyName,
        string phaseName,
        Vector2 playerGridPos,
        IReadOnlyList<(string Label, string Value)>? extraRows = null)
    {
        // --- Window setup: top-center, semi-transparent, non-interactive ---
        var viewport = ImGui.GetMainViewport();
        var windowPos = new Vector2(viewport.Pos.X + viewport.Size.X * 0.5f - WindowWidth * 0.5f, viewport.Pos.Y + 10f);

        ImGui.SetNextWindowPos(windowPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(WindowWidth, 0), ImGuiCond.Always); // auto-height
        ImGui.SetNextWindowBgAlpha(0.60f);

        var flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin("##NightmareOverlay", flags))
        {
            ImGui.End();
            return;
        }

        // --- Header ---
        DrawHeader(state);
        ImGui.Separator();

        // --- Status rows ---
        DrawLabelValue("Strategy", strategyName);
        DrawLabelValue("Phase", phaseName);
        DrawLabelValue("Player", $"{playerGridPos.X:F0}, {playerGridPos.Y:F0}");

        if (extraRows != null)
        {
            foreach (var (label, value) in extraRows)
                DrawLabelValue(label, value);
        }

        ImGui.Separator();

        // --- Recent log ---
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Recent Activity");

        lock (_lock)
        {
            foreach (var entry in _logBuffer)
            {
                var timestamp = entry.Time.ToString("HH:mm:ss");
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), timestamp);
                ImGui.SameLine();
                ImGui.TextWrapped(entry.Message);
            }
        }

        ImGui.End();
    }

    private static void DrawHeader(BotState state)
    {
        var color = state switch
        {
            BotState.Idle => new Vector4(0.6f, 0.6f, 0.6f, 1f),
            BotState.RunningFarm => new Vector4(0.3f, 1f, 0.3f, 1f),
            BotState.Finished => new Vector4(0.4f, 0.7f, 1f, 1f),
            BotState.Dead => new Vector4(1f, 0.2f, 0.2f, 1f),
            BotState.Disconnected => new Vector4(1f, 0.5f, 0.2f, 1f),
            BotState.Error => new Vector4(1f, 0f, 0f, 1f),
            _ => new Vector4(0.8f, 0.8f, 1f, 1f),
        };

        ImGui.TextColored(color, $"● {state}");
        ImGui.SameLine(WindowWidth - 100f);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "Nightmare");
    }

    private static void DrawLabelValue(string label, string value)
    {
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"{label}:");
        ImGui.SameLine(90f);
        ImGui.Text(value);
    }

    private readonly record struct LogEntry(DateTime Time, string Message);
}
