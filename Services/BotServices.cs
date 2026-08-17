using NightmareAutomate.Engine;
using NightmareAutomate.Models;

namespace NightmareAutomate.Services;

/// <summary>
/// Everything the engine and strategies consume, built once in the plugin's
/// Initialise. Cuts the 10-11 parameter constructors down to one bundle;
/// individual services keep their own focused constructors.
/// </summary>
public sealed record BotServices(
    GameStateService GameState,
    InputService Input,
    InventoryService Inventory,
    NavigationService Navigation,
    MapDeviceService MapDevice,
    LootService Loot,
    StashService Stash,
    ExplorationService Exploration,
    GemLevelService Gems,
    ChoresService Chores,
    EventRecovery Recovery,
    DiscordWebhookService Webhook,
    SessionStats Stats,
    AreaMemory Areas);
