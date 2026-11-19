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

/// <summary>
/// SettingsDrawer draws all of these. IgnoreMenu keeps the HUD's settings parser off
/// the plain values, or it logs a warning with each name and value at every start.
/// </summary>
public class NightmareAutomateSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [JsonIgnore]
    public CustomNode SettingsUi { get; set; }

    /// <summary>Live stash tab names for the dump-tab picker; wired up by the plugin in Initialise.</summary>
    [JsonIgnore]
    [IgnoreMenu]
    public Func<IReadOnlyList<string>> StashTabNames { get; set; } = () => [];

    public NightmareAutomateSettings()
    {
        SettingsUi = new CustomNode(new SettingsDrawer(this).Draw);
    }

    // --- Control ---

    [IgnoreMenu]
    public HotkeyNodeV2 ToggleButton { get; set; } = new HotkeyNodeV2(Keys.F8);

    /// <summary>"Stop after this run" state for the Main tab: null = the bot is not
    /// running, otherwise whether the stop is armed. Wired up by the plugin in Initialise.</summary>
    [JsonIgnore]
    [IgnoreMenu]
    public Func<bool?>? StopAfterRunPending { get; set; }

    /// <summary>Arms / disarms "stop after this run"; wired up by the plugin in Initialise.</summary>
    [JsonIgnore]
    [IgnoreMenu]
    public Action? ToggleStopAfterRun { get; set; }

    [IgnoreMenu]
    public HotkeyNodeV2 MoveKey { get; set; } = new HotkeyNodeV2(Keys.Space);

    [IgnoreMenu]
    public HotkeyNodeV2 InventoryKey { get; set; } = new HotkeyNodeV2(Keys.I);

    [IgnoreMenu]
    public HotkeyNodeV2 HideoutKey { get; set; } = new HotkeyNodeV2(Keys.F5);

    // --- Input ---

    /// <summary>Keep running while the game window isn't focused. Only takes effect
    /// with "Use Background Input" also on in InputHumanizer: that is what sends
    /// input into the game instead of through the OS.</summary>
    [IgnoreMenu]
    public bool UseBackgroundInput { get; set; } = false;

    /// <summary>The checkbox is on but InputHumanizer's isn't; wired up by the plugin
    /// in Initialise so the settings UI can say so.</summary>
    [JsonIgnore]
    [IgnoreMenu]
    public Func<bool>? BackgroundInputMismatch { get; set; }

    /// <summary>Take the HUD's overlay off the screen while another window is in front.</summary>
    [IgnoreMenu]
    public bool HideHudWhileUnfocused { get; set; } = true;

    // --- Farm Selection ---

    [JsonConverter(typeof(StringEnumConverter))]
    [IgnoreMenu]
    public Enums.FarmType FarmType { get; set; } = Enums.FarmType.Simulacrum;

    [IgnoreMenu]
    public int MaxRuns { get; set; } = 0;

    // --- Navigation ---

    [IgnoreMenu]
    public int PathLookahead { get; set; } = 20;

    // --- Recovery ---

    /// <summary>Enter presses to attempt after a disconnect before the session is
    /// stopped (cadence escalates 5s → 15s → 30s; waiting for window focus or on
    /// loading screens does not count). 0 = keep trying forever.</summary>
    [IgnoreMenu]
    public int MaxReconnectAttempts { get; set; } = 150;

    // --- Simulacrum ---

    [IgnoreMenu]
    public int HoverRange { get; set; } = 35;

    [IgnoreMenu]
    public int UniqueHoverRange { get; set; } = 45;

    [IgnoreMenu]
    public int NearTargetRadius { get; set; } = 80;

    [IgnoreMenu]
    public int ClusterRadius { get; set; } = 15;

    /// <summary>How many clustered monsters one rarity step is worth in target
    /// scoring: a magic with a pack of this size+1 outranks a lone rare; a white
    /// pack needs twice that. Uniques/totems ignore this (always top priority).</summary>
    [IgnoreMenu]
    public int PackSizePerTier { get; set; } = 4;

    /// <summary>How long the current target is held before an equal-priority pick
    /// may replace it (dying/leaving the pool/higher priority switch instantly).</summary>
    [IgnoreMenu]
    public int TargetStickSeconds { get; set; } = 2;

    [IgnoreMenu]
    public bool MoveCloseToProximityShieldRares { get; set; } = true;

    [IgnoreMenu]
    public bool NoMovementDuringAbilities { get; set; } = true;

    [IgnoreMenu]
    public int LootSpawnWaitSeconds { get; set; } = 5;

    [IgnoreMenu]
    public int LateLootLingerSeconds { get; set; } = 5;

    /// <summary>Hard per-run time limit in minutes: past it the run is abandoned
    /// (leave, treat as completed, open the next Simulacrum). Guards against
    /// effectively-unkillable rare mod rolls. 0 = no limit.</summary>
    [IgnoreMenu]
    public int AbandonRunAfterMinutes { get; set; } = 45;

    // --- Automation ---

    [IgnoreMenu]
    public bool LevelGems { get; set; } = true;

    /// <summary>Stash tab to restock Simulacrums from when the map device slot and
    /// its storage run dry. Empty = no restocking (session ends when out).</summary>
    [IgnoreMenu]
    public string SimulacrumTabName { get; set; } = "";

    /// <summary>Carry an Omen of Amelioration at all times: it is never dumped into
    /// the stash, and when a death consumes it a fresh one is taken from the
    /// restock tab (SimulacrumTabName) — if the tab has one.</summary>
    [IgnoreMenu]
    public bool KeepOmenOfAmelioration { get; set; } = false;

    // --- Notifications ---

    /// <summary>A secret: anyone holding it can post to the channel, so it especially
    /// must stay out of the logs. The settings file still saves it.</summary>
    [IgnoreMenu]
    public string DiscordWebhookUrl { get; set; } = "";

    [IgnoreMenu]
    public bool NotifyCritical { get; set; } = true;

    [IgnoreMenu]
    public bool NotifyRunSummary { get; set; } = true;

    /// <summary>Optional text prepended to critical alerts (e.g. "&lt;@userid&gt;"
    /// or "@everyone") so Discord pings you. Empty = no mention.</summary>
    [IgnoreMenu]
    public string DiscordMentionOnCritical { get; set; } = "";

    /// <summary>Settings-UI test button hook; wired up by the plugin in Initialise.</summary>
    [JsonIgnore]
    [IgnoreMenu]
    public Action? SendTestNotification { get; set; }

    // --- Debug ---

    /// <summary>Log the visible Atlas UI tree (rects, textures, entity bindings)
    /// to the file log — for identifying moved/unknown elements like the storage
    /// collapse toggle. Wired up by the plugin in Initialise.</summary>
    [JsonIgnore]
    [IgnoreMenu]
    public Action? DumpAtlasUi { get; set; }

    /// <summary>Also write routine steps to the log file, not only outcomes and problems.</summary>
    [IgnoreMenu]
    public bool VerboseLogs { get; set; } = false;

    // --- Loot & Stash ---

    [IgnoreMenu]
    public HotkeyNodeV2 ToggleLabelsKey { get; set; } = new HotkeyNodeV2(Keys.Z);

    [IgnoreMenu]
    public string DumpTabNames { get; set; } = "Dump";

    // --- Debug ---

    [IgnoreMenu]
    public bool DebugOverlay { get; set; } = true;
}
