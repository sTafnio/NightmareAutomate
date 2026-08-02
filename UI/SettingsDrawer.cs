using System;
using System.Linq;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using NightmareAutomate.Enums;

namespace NightmareAutomate.UI;

/// <summary>
/// Draws the whole settings window as a tab bar (Main / Settings).
/// Hooked in through a CustomNode on the settings class, so the plugin class
/// needs no DrawSettings override and the default reflection-generated menu
/// stays empty. Details live in (?) hover tooltips, labels stay short.
/// </summary>
public class SettingsDrawer
{
    private const float ControlWidth = 220f;

    private readonly NightmareAutomateSettings _s;

    public SettingsDrawer(NightmareAutomateSettings settings)
    {
        _s = settings;
    }

    public void Draw()
    {
        if (!ImGui.BeginTabBar("##na_tabs")) return;

        Tab("Main", DrawMainTab);
        Tab("Settings", DrawSettingsTab);

        ImGui.EndTabBar();
    }

    private static void Tab(string label, Action drawBody)
    {
        if (!ImGui.BeginTabItem(label)) return;

        ImGui.Spacing();
        drawBody();
        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // --- Main tab ---

    private void DrawMainTab()
    {
        ImGui.SeparatorText("Farm");

        foreach (var farmType in Enum.GetValues<FarmType>())
        {
            if (ImGui.RadioButton(farmType.ToString(), _s.FarmType == farmType))
                _s.FarmType = farmType;
        }

        ImGui.Spacing();
        _s.MaxRuns = Slider("Max Runs", _s.MaxRuns, 0, 200,
            "Stop after this many completed runs. 0 = unlimited.",
            _s.MaxRuns == 0 ? "unlimited" : "%d");

        ImGui.Spacing();
        ImGui.SeparatorText("Automation");

        _s.LevelGems = Toggle("Level Gems", _s.LevelGems,
            "Click gem level-ups at safe moments (after wave loot, before waves, in the hideout). " +
            "Gems whose popup says 'Gem cannot level up' are left alone.");

        _s.SimulacrumTabName = TabPicker("Simulacrum Tab", _s.SimulacrumTabName,
            "Stash tab to restock Simulacrums from when the map device and its storage run out. " +
            "'(none)' = no restocking; the session ends when out of fragments.");

        ImGui.Spacing();
        ImGui.SeparatorText("Dump Tabs");

        DrawDumpTabPicker();
    }

    /// <summary>
    /// Single stash tab picker: combo over the live tab list with a "(none)" entry
    /// (empty string = feature off). Falls back to a raw text input while the live
    /// list is unavailable.
    /// </summary>
    private string TabPicker(string label, string current, string? help)
    {
        var tabNames = _s.StashTabNames();
        if (tabNames.Count == 0)
            return TextInput(label, current, help);

        current ??= string.Empty;
        ImGui.SetNextItemWidth(ControlWidth);
        // BeginCombo so a saved name missing from the live list (renamed/other
        // league) still shows as the preview value.
        if (ImGui.BeginCombo(label, string.IsNullOrEmpty(current) ? "(none)" : current))
        {
            if (ImGui.Selectable("(none)", string.IsNullOrEmpty(current)))
                current = string.Empty;

            foreach (var name in tabNames)
            {
                var isCurrent = name.Equals(current, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(name, isCurrent))
                    current = name;

                if (isCurrent)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        HelpMarker(help);
        return current;
    }

    /// <summary>
    /// One combo row per configured dump tab (row order = dump order), an X to
    /// remove a row and an Add button to append one. Falls back to the raw
    /// comma-separated text input while the live tab list is unavailable.
    /// </summary>
    private void DrawDumpTabPicker()
    {
        var tabNames = _s.StashTabNames();
        if (tabNames.Count == 0)
        {
            ImGui.TextDisabled("Stash tab list unavailable -- enter the game / visit your stash once.");
            _s.DumpTabNames = TextInput("Dump Tab Names", _s.DumpTabNames,
                "Comma-separated stash tabs to dump loot into, in order. " +
                "Affinities sort items automatically; a full tab advances to the next.");
            return;
        }

        var selected = _s.DumpTabNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var changed = false;
        var removeAt = -1;

        for (var i = 0; i < selected.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.TextUnformatted($"Tab {i + 1}");
            ImGui.SameLine(90);
            ImGui.SetNextItemWidth(ControlWidth);
            // BeginCombo instead of Combo so a saved name missing from the live
            // list (renamed/other league) still shows as the preview value.
            if (ImGui.BeginCombo("##dumptab", selected[i]))
            {
                foreach (var name in tabNames)
                {
                    var isCurrent = name.Equals(selected[i], StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(name, isCurrent) && !isCurrent)
                    {
                        selected[i] = name;
                        changed = true;
                    }

                    if (isCurrent)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.Button("X"))
                removeAt = i;
            ImGui.PopID();
        }

        if (removeAt >= 0)
        {
            selected.RemoveAt(removeAt);
            changed = true;
        }

        if (selected.Count == 0)
            ImGui.TextDisabled("No dump tabs -- loot stashing disabled.");

        if (ImGui.Button("Add Tab"))
        {
            selected.Add(tabNames.FirstOrDefault(n =>
                !selected.Contains(n, StringComparer.OrdinalIgnoreCase)) ?? tabNames[0]);
            changed = true;
        }

        HelpMarker("Loot is dumped into these tabs in order; affinities sort items " +
                   "automatically and a full tab advances to the next.");

        if (changed)
            _s.DumpTabNames = string.Join(",", selected);
    }

    // --- Settings tab ---

    private void DrawSettingsTab()
    {
        ImGui.SeparatorText("Hotkeys");

        if (ImGui.BeginTable("##hotkeys", 2, ImGuiTableFlags.SizingFixedFit))
        {
            HotkeyRow("Start / Stop", _s.ToggleButton, null);
            HotkeyRow("Move", _s.MoveKey, "Key the bot holds down to move.");
            HotkeyRow("Inventory", _s.InventoryKey,
                "Must match the game keybind that opens the inventory panel.");
            HotkeyRow("Hideout", _s.HideoutKey,
                "Must match a game Command Shortcut bound to /hideout (Options > Input).");
            HotkeyRow("Loot Labels", _s.ToggleLabelsKey,
                "Must match the game keybind that shows/hides loot labels " +
                "(used to re-render offscreen labels).");
            ImGui.EndTable();
        }

        ImGui.SeparatorText("General");

        _s.MoveCloseToProximityShieldRares = Toggle("Engage Proximity Rares Close",
            _s.MoveCloseToProximityShieldRares,
            "Engage rares with a Temporal Bubble aura (MonsterModTemporalAura) " +
            "from up close (distance 15) instead of the normal hover range.");
        _s.NoMovementDuringAbilities = Toggle("No Movement During Abilities",
            _s.NoMovementDuringAbilities,
            "While the character is mid-ability (Actor.isAttacking), no NEW walk is started " +
            "so the animation is not cancelled -- walks already running continue. " +
            "Totem/proximity/unique targets still start walks regardless.");
        _s.DebugOverlay = Toggle("Debug Overlay", _s.DebugOverlay,
            "Show the current bot state and strategy on screen.");
        _s.PathLookahead = Slider("Path Lookahead", _s.PathLookahead, 1, 50,
            "How many path nodes ahead to target for movement.");

        ImGui.SeparatorText("Recovery");

        _s.MaxReconnectAttempts = Slider("Max Relogin Attempts", _s.MaxReconnectAttempts, 0, 1000,
            "After a disconnect, how many Enter presses to attempt before giving up and stopping " +
            "the session. The cadence backs off (5s for 2 min, then 15s, then 30s), so the default " +
            "of 150 gives up after roughly an hour. Waiting for window focus or on loading screens " +
            "does not count. 0 = keep trying forever.",
            _s.MaxReconnectAttempts == 0 ? "forever" : "%d");

        ImGui.SeparatorText("Targeting & Combat");

        _s.HoverRange = Slider("Hover Range", _s.HoverRange, 10, 100,
            "Stop moving and hover the target within this distance (ReAgent kills).");
        _s.UniqueHoverRange = Slider("Unique Hover Range", _s.UniqueHoverRange, 10, 150,
            "Preferred distance to unique monsters (Kosis/Omniphobia). Higher = stay further away.");
        _s.NearTargetRadius = Slider("Near Target Radius", _s.NearTargetRadius, 20, 250,
            "Monsters within this range are always fought before anything farther away -- " +
            "prevents running off through packs to a distant rare.");
        _s.ClusterRadius = Slider("Cluster Radius", _s.ClusterRadius, 5, 40,
            "Pack-density radius: monsters within this range of a candidate count toward its " +
            "cluster score -- the bot hovers the pack center for AOE value.");
        _s.PackSizePerTier = Slider("Pack Size Per Tier", _s.PackSizePerTier, 1, 12,
            "How many clustered monsters equal one rarity step. Example at 4: a magic monster " +
            "with 5+ monsters packed around it outranks a lone rare; a white pack needs 9+. " +
            "Lower = packs win more often. Uniques and ACD totems always stay on top.");
        _s.TargetStickSeconds = Slider("Target Stick (s)", _s.TargetStickSeconds, 0, 10,
            "Hold the current target this long before an equal-priority candidate may replace " +
            "it -- prevents hover flip-flopping. Death, leaving the near band, or a " +
            "higher-priority target (totem/unique) switches instantly.");

        ImGui.SeparatorText("Simulacrum");

        _s.LootSpawnWaitSeconds = Slider("Loot Spawn Wait (s)", _s.LootSpawnWaitSeconds, 1, 15,
            "After a wave ends, wait at the start location up to this long for the loot to spawn in.");
        _s.LateLootLingerSeconds = Slider("Late Loot Linger (s)", _s.LateLootLingerSeconds, 0, 15,
            "After a wave's loot is fully picked, keep watching the ground this long for " +
            "late-spawning items (the game sometimes spawns a single item well after the rest).");
        _s.AbandonRunAfterMinutes = Slider("Abandon Run After (min)", _s.AbandonRunAfterMinutes, 0, 180,
            "Hard time limit per run, counted from map device activation (deaths and hideout trips " +
            "included). Past it the run is abandoned: leave the map, treat the run as completed, " +
            "open the next Simulacrum. Guards against unkillable rare mod rolls. 0 = no limit.",
            _s.AbandonRunAfterMinutes == 0 ? "off" : "%d");

        ImGui.SeparatorText("Discord Notifications");

        _s.DiscordWebhookUrl = TextInput("Webhook URL", _s.DiscordWebhookUrl,
            "Discord channel webhook URL (channel settings > Integrations > Webhooks). " +
            "Empty = notifications off.");
        _s.NotifyCritical = Toggle("Critical Alerts", _s.NotifyCritical,
            "Disconnects/reconnects, stash trouble, lost/abandoned runs, bot stopped or errored.");
        _s.NotifyRunSummary = Toggle("Run Summaries", _s.NotifyRunSummary,
            "One message per completed run (waves, duration, deaths, gems, portals) " +
            "plus a session summary when the bot finishes.");
        _s.DiscordMentionOnCritical = TextInput("Mention On Critical", _s.DiscordMentionOnCritical,
            "Optional mention prepended to critical alerts so Discord pings you: " +
            "<@your-user-id> or @everyone. Empty = no ping.");
        if (ImGui.Button("Send Test Message"))
            _s.SendTestNotification?.Invoke();
        HelpMarker("Posts a test embed to the configured webhook (also resets the failure pause).");
    }

    // --- Widgets ---

    private static bool Toggle(string label, bool value, string? help)
    {
        ImGui.Checkbox(label, ref value);
        HelpMarker(help);
        return value;
    }

    private static int Slider(string label, int value, int min, int max, string? help, string format = "%d")
    {
        ImGui.SetNextItemWidth(ControlWidth);
        ImGui.SliderInt(label, ref value, min, max, format);
        HelpMarker(help);
        return value;
    }

    private static string TextInput(string label, string value, string? help)
    {
        ImGui.SetNextItemWidth(ControlWidth);
        value ??= string.Empty;
        ImGui.InputText(label, ref value, 512);
        HelpMarker(help);
        return value;
    }

    private static void HotkeyRow(string label, HotkeyNodeV2 node, string? help)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);

        ImGui.TableNextColumn();
        node.DrawPickerButton($"{node.Value}##hk_{label}");
        HelpMarker(help);
    }

    /// <summary>Dim "(?)" appended to the previous widget's line; tooltip on hover. Null = no marker.</summary>
    private static void HelpMarker(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(420f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
