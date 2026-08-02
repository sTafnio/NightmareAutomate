using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NightmareAutomate.UI;

namespace NightmareAutomate;

public class NightmareAutomateSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [JsonIgnore]
    public CustomNode SettingsUi { get; set; }

    /// <summary>Live stash tab names for the dump-tab picker; wired up by the plugin in Initialise.</summary>
    [JsonIgnore]
    public Func<IReadOnlyList<string>> StashTabNames { get; set; } = () => [];

    public NightmareAutomateSettings()
    {
        SettingsUi = new CustomNode(new SettingsDrawer(this).Draw);
    }

    // --- Control ---

    [IgnoreMenu]
    public HotkeyNodeV2 ToggleButton { get; set; } = new HotkeyNodeV2(Keys.F8);

    [IgnoreMenu]
    public HotkeyNodeV2 MoveKey { get; set; } = new HotkeyNodeV2(Keys.Space);

    [IgnoreMenu]
    public HotkeyNodeV2 InventoryKey { get; set; } = new HotkeyNodeV2(Keys.I);

    [IgnoreMenu]
    public HotkeyNodeV2 HideoutKey { get; set; } = new HotkeyNodeV2(Keys.F5);

    // --- Farm Selection ---

    [JsonConverter(typeof(StringEnumConverter))]
    public Enums.FarmType FarmType { get; set; } = Enums.FarmType.Simulacrum;

    public int MaxRuns { get; set; } = 0;

    // --- Navigation ---

    public int PathLookahead { get; set; } = 20;

    // --- Recovery ---

    /// <summary>Enter presses to attempt after a disconnect before the session is
    /// stopped (cadence escalates 5s → 15s → 30s; waiting for window focus or on
    /// loading screens does not count). 0 = keep trying forever.</summary>
    public int MaxReconnectAttempts { get; set; } = 150;

    // --- Simulacrum ---

    public int HoverRange { get; set; } = 35;

    public int UniqueHoverRange { get; set; } = 45;

    public int NearTargetRadius { get; set; } = 80;

    public int ClusterRadius { get; set; } = 15;

    /// <summary>How many clustered monsters one rarity step is worth in target
    /// scoring: a magic with a pack of this size+1 outranks a lone rare; a white
    /// pack needs twice that. Uniques/totems ignore this (always top priority).</summary>
    public int PackSizePerTier { get; set; } = 4;

    /// <summary>How long the current target is held before an equal-priority pick
    /// may replace it (dying/leaving the pool/higher priority switch instantly).</summary>
    public int TargetStickSeconds { get; set; } = 2;

    public bool MoveCloseToProximityShieldRares { get; set; } = true;

    public bool NoMovementDuringAbilities { get; set; } = true;

    public int LootSpawnWaitSeconds { get; set; } = 5;

    public int LateLootLingerSeconds { get; set; } = 5;

    /// <summary>Hard per-run time limit in minutes: past it the run is abandoned
    /// (leave, treat as completed, open the next Simulacrum). Guards against
    /// effectively-unkillable rare mod rolls. 0 = no limit.</summary>
    public int AbandonRunAfterMinutes { get; set; } = 45;

    // --- Automation ---

    public bool LevelGems { get; set; } = true;

    /// <summary>Stash tab to restock Simulacrums from when the map device slot and
    /// its storage run dry. Empty = no restocking (session ends when out).</summary>
    public string SimulacrumTabName { get; set; } = "";

    // --- Notifications ---

    public string DiscordWebhookUrl { get; set; } = "";

    public bool NotifyCritical { get; set; } = true;

    public bool NotifyRunSummary { get; set; } = true;

    /// <summary>Optional text prepended to critical alerts (e.g. "&lt;@userid&gt;"
    /// or "@everyone") so Discord pings you. Empty = no mention.</summary>
    public string DiscordMentionOnCritical { get; set; } = "";

    /// <summary>Settings-UI test button hook; wired up by the plugin in Initialise.</summary>
    [JsonIgnore]
    public Action? SendTestNotification { get; set; }

    // --- Loot & Stash ---

    [IgnoreMenu]
    public HotkeyNodeV2 ToggleLabelsKey { get; set; } = new HotkeyNodeV2(Keys.Z);

    public string DumpTabNames { get; set; } = "Dump";

    // --- Debug ---

    public bool DebugOverlay { get; set; } = true;
}
