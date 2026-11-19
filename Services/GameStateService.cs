using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using NightmareAutomate.Helpers;
using SharpDX;

namespace NightmareAutomate.Services;

/// <summary>
/// Reads game state from GameController behind a clean, testable API.
/// </summary>
public sealed class GameStateService(GameController gc)
{
    private const string MapDevicePathFragment = "MappingDevice";
    private const string StashPathFragment = "MiscellaneousObjects/Stash";

    private readonly GameController _gc = gc;

    public GameController Controller => _gc;

    public bool IsReady => _gc.InGame && !_gc.IsLoading && _gc.Player != null && _gc.Player.IsValid;
    public string AreaName => (_gc.InGame && _gc.Area?.CurrentArea != null) ? _gc.Area.CurrentArea.Name : "Unknown";

    /// <summary>Hash identifying the current AREA INSTANCE — two visits to the same
    /// map name get different hashes. 0 when unavailable.</summary>
    public uint AreaHash => _gc.InGame ? _gc.Area?.CurrentArea?.Hash ?? 0 : 0;
    public bool IsInHideout => IsReady && (_gc.Area?.CurrentArea?.IsHideout ?? false);
    public bool IsInTown => IsReady && (_gc.Area?.CurrentArea?.IsTown ?? false);
    public bool IsPlayerDead => IsReady && (_gc.Player?.IsDead ?? false);
    public bool IsConnected => _gc.InGame;
    public bool IsLoading => _gc.IsLoading;

    /// <summary>The login or character-select screen is active — the definitive
    /// "we were disconnected" signal. Read straight off the game-state machine:
    /// InGame/IsLoading are host-maintained caches whose values on those screens
    /// are not contractual, so recovery must not depend on them alone.</summary>
    public bool IsAtLoginScreen => SafeRead.Try("gs.login-screen", () =>
    {
        var game = _gc.Game;
        return game.IsLoginState || game.IsSelectCharacterState;
    });

    /// <summary>An InGameState is active on the game-state machine — the relogin
    /// loop's exit signal (same condition the proven Automate relogin uses).
    /// True before the world is fully streamed in; gate actions on IsReady.</summary>
    public bool IsInGameState => SafeRead.Try("gs.ingame-state", () => _gc.Game.IsInGameState);

    /// <summary>An area-loading screen is active on the game-state machine
    /// (login -> world, or between areas). Distinct from IsLoading, which is the
    /// host's cached view and unreliable outside a session.</summary>
    public bool IsLoadingState => SafeRead.Try("gs.loading-state", () => _gc.Game.IsLoadingState);

    /// <summary>Whether the game window has OS focus — OS input only reaches the game
    /// then (relevant for login-screen recovery, where nothing gates on InGame).</summary>
    public bool IsWindowForeground => SafeRead.Try("gs.window-foreground", () => _gc.Window.IsForeground());

    /// <summary>
    /// Whether a click-swallowing UI panel is open: any visible fullscreen/large
    /// panel the game tracks, or the generic popup window (wave-reward/modifier
    /// dialogs). Used to decide whether pressing Escape would CLOSE something —
    /// Escape with nothing open brings up the game menu instead.
    /// </summary>
    public bool HasBlockingUiPanel => SafeRead.Try("gs.blocking-panel", () =>
    {
        var ui = _gc.IngameState?.IngameUi;
        if (ui == null) return false;
        if (ui.PopUpWindow?.IsVisible == true) return true;
        if (ui.FullscreenPanels?.Any(p => p?.IsVisible == true) == true) return true;
        if (ui.LargePanels?.Any(p => p?.IsVisible == true) == true) return true;
        return false;
    });

    /// <summary>
    /// A panel the bot NEVER wants open in any state — the MTX shop, passive
    /// tree, world map, social or settings panel, opened by a stray click or
    /// keypress landing on the game's own UI. Unlike HasBlockingUiPanel (whose
    /// list includes sometimes-legitimate popups like the wave-reward window),
    /// Escape is always the right answer for these, so a watchdog may close
    /// them unconditionally.
    /// </summary>
    public bool HasStrayPanelOpen => SafeRead.Try("gs.stray-panel", () =>
    {
        var ui = _gc.IngameState?.IngameUi;
        if (ui == null) return false;
        return ui.MicrotransactionShopWindow?.IsVisible == true
            || ui.TreePanel?.IsVisible == true
            || ui.WorldMap?.IsVisible == true
            || ui.SocialPanel?.IsVisible == true
            || ui.SettingsPanel?.IsVisible == true;
    });

    private uint _trackedAreaHash;
    private long _areaChangedAt = Clock.Never;

    /// <summary>Seconds since the bot last saw the area change, either as a host
    /// refresh (the plugin's AreaChange) or as a new area hash polled each Tick.
    /// The host skips its area refresh when an area change follows the previous
    /// one by less than ~5 s (measured over 125 death re-entries in the logs:
    /// every re-entry under 5 s got no refresh). The farm-portal entry waits on
    /// this so a quick hop back into the map is not left with unrefreshed host data.</summary>
    public double SecondsSinceAreaChange => Clock.SecondsSince(_areaChangedAt);

    /// <summary>The host refreshed the area (plugin AreaChange, which also fires on forced refreshes).</summary>
    public void NoteAreaChange() => _areaChangedAt = Clock.NowMs;

    /// <summary>Per-Tick poll: catches area changes the host did not refresh for
    /// (CurrentArea itself still updates then).</summary>
    public void TrackAreaChange()
    {
        var hash = SafeRead.Try("gs.area-hash", () => AreaHash);
        if (hash == 0 || hash == _trackedAreaHash) return;
        _trackedAreaHash = hash;
        _areaChangedAt = Clock.NowMs;
    }

    private string? _characterName;
    private string? _accountName;
    private long _identityCheckAt;

    /// <summary>"Character (Account)" of the logged-in player, for telling several
    /// bots apart in one notification channel. Null until the first in-game read.
    /// Keeps the last good value, so alerts raised while disconnected or on the
    /// login screen still name the bot that sent them.</summary>
    public string? PlayerIdentity { get; private set; }

    /// <summary>Per-Tick, self-throttled to every 10 s: re-reads the character
    /// name (a character switch goes through character select), and looks up the
    /// account only when it is still unknown or the character changed. ExileCore
    /// has no local account name; the local player is listed in
    /// ServerData.KnownPlayers, matched by character name (unique game-wide).</summary>
    public void TrackPlayerIdentity()
    {
        if (!Clock.Reached(_identityCheckAt)) return;
        _identityCheckAt = Clock.InSeconds(10);
        if (!IsReady) return;

        var character = SafeRead.Try("gs.character-name",
            () => _gc.Player?.GetComponent<Player>()?.PlayerName);
        if (string.IsNullOrWhiteSpace(character)) return;

        if (character != _characterName)
        {
            _characterName = character;
            _accountName = null;
        }

        _accountName ??= SafeRead.Try("gs.account-name", () =>
            _gc.IngameState?.Data?.ServerData?.KnownPlayers?
                .FirstOrDefault(p => p?.CharacterName == character)?.AccountName);
        if (string.IsNullOrWhiteSpace(_accountName))
            _accountName = null;

        PlayerIdentity = _accountName != null ? $"{_characterName} ({_accountName})" : _characterName;
    }

    private long _lastAreaRefreshAt = Clock.Never;

    /// <summary>Force the host to re-read the current area and rebuild its caches
    /// (entity list, terrain, area name/hash) — the recovery for a MISSED area
    /// change, where searches run against a stale world while IsReady looks fine.
    /// Heavy (re-fires AreaChange for every plugin, Radar re-paths etc.), so it is
    /// throttled to once per 10s and meant for retry/escalation paths only, never
    /// routine checks. Returns whether the refresh actually fired; callers should
    /// give the host a moment (~1s) to rebuild before re-checking.</summary>
    public bool TryForceRefreshArea()
    {
        if (Clock.SecondsSince(_lastAreaRefreshAt) < 10) return false;
        _lastAreaRefreshAt = Clock.NowMs;
        return SafeRead.Try("gs.force-refresh", () =>
        {
            _gc.Area.ForceRefreshArea(true);
            return true;
        });
    }

    /// <summary>Whether the Atlas panel (which embeds the map device UI) is open.
    /// The Atlas and other panels (stash, ...) are mutually exclusive — opening
    /// one closes the other — so a flow that needs ITS panel while the Atlas is
    /// up must Escape the Atlas first and then REOPEN its own panel.</summary>
    public bool IsAtlasOpen =>
        SafeRead.Try("gs.atlas-open", () => _gc.IngameState?.IngameUi?.Atlas?.IsVisible == true);

    /// <summary>The generic popup window (wave-reward/modifier dialogs) alone —
    /// the one panel class that can sit ABOVE the map device window and swallow
    /// its clicks without the device UI itself counting as "blocking".</summary>
    public bool HasPopupOpen =>
        SafeRead.Try("gs.popup-open", () => _gc.IngameState?.IngameUi?.PopUpWindow?.IsVisible == true);

    /// <summary>
    /// The screen area (window-relative) currently covered by click-swallowing UI:
    /// the union of every visible popup / large / fullscreen panel the game tracks.
    /// Empty when nothing readable is up — which does NOT prove the screen is
    /// clear (the Simulacrum wave-start dialog is not in any of those lists), so
    /// callers must treat it as a hint and still verify by hovering.
    /// </summary>
    public RectangleF BlockingUiRect => SafeRead.Try("gs.blocking-ui-rect", () =>
    {
        var ui = _gc.IngameState?.IngameUi;
        if (ui == null) return RectangleF.Empty;

        var union = RectangleF.Empty;
        void Add(Element? e)
        {
            if (e?.IsVisible != true) return;
            var r = e.GetClientRect();
            if (r.Width <= 0 || r.Height <= 0) return;
            union = union.IsEmpty ? r : RectangleF.Union(union, r);
        }

        Add(ui.PopUpWindow);
        foreach (var p in ui.LargePanels ?? []) Add(p);
        foreach (var p in ui.FullscreenPanels ?? []) Add(p);
        return union;
    });

    /// <summary>
    /// Screen displacement (window-relative pixels) that moving the character by a
    /// GRID delta produces. The camera rides the character, so a world point's
    /// screen position shifts by the NEGATIVE of this — which is what makes
    /// "step aside so the target stops projecting under the panel" computable
    /// without walking there first to look.
    /// </summary>
    public System.Numerics.Vector2 GridDeltaToScreenDelta(System.Numerics.Vector2 gridDelta)
    {
        const float gridToWorld = 250f / 23f;
        var from = PlayerWorldPos;
        var to = new System.Numerics.Vector3(
            from.X + gridDelta.X * gridToWorld,
            from.Y + gridDelta.Y * gridToWorld,
            from.Z);
        return WorldToScreen(to) - WorldToScreen(from);
    }

    /// <summary>The window-relative screen point an entity's interact center
    /// projects to right now.</summary>
    public System.Numerics.Vector2 EntityScreenPoint(Entity entity)
    {
        var center = entity.GetComponent<Render>()?.InteractCenterNum ?? entity.PosNum;
        return WorldToScreen(center);
    }

    /// <summary>
    /// One-line diagnosis of what the cursor is ACTUALLY on: the world entity
    /// holding the hover highlight (if any) and whether a UI element sits under
    /// the cursor. Used when an intended click target never highlights — the
    /// identity of the thief (a minion, an NPC, a panel edge) is the diagnosis.
    /// Walks the entity list with per-entity guards; only called on failure paths.
    /// </summary>
    public string DescribeHoverTarget()
    {
        try
        {
            var playerPos = PlayerGridPos;
            Entity? highlighted = null;
            foreach (var e in _gc.EntityListWrapper.Entities)
            {
                try
                {
                    if (e?.GetComponent<Targetable>()?.isTargeted == true)
                    {
                        highlighted = e;
                        break;
                    }
                }
                catch
                {
                    // Entity mid-update -- skip it.
                }
            }

            var s = highlighted != null
                ? $"[hover: highlight is on '{highlighted.Path}' " +
                  $"({(int)System.Numerics.Vector2.Distance(playerPos, highlighted.GridPosNum)} units from player)"
                : "[hover: no world entity is highlighted";

            var uiHover = _gc.IngameState?.UIHoverElement;
            if (uiHover != null && uiHover.Address != 0 && uiHover.IsVisible)
                s += $"; a UI element is under the cursor ({uiHover.GetType().Name})";

            return s + "]";
        }
        catch (Exception ex)
        {
            return $"[hover state unreadable: {ex.Message}]";
        }
    }

    /// <summary>Whether the character is currently moving (Actor component).</summary>
    public bool IsPlayerMoving => _gc.Player?.GetComponent<Actor>()?.isMoving == true;

    /// <summary>Whether the character is currently using an ability (Actor component).</summary>
    public bool IsPlayerAttacking => _gc.Player?.GetComponent<Actor>()?.isAttacking == true;

    /// <summary>
    /// Server-side count of remaining encounter monsters (Simulacrum waves report
    /// through this). Readable regardless of entity streaming.
    /// </summary>
    public int MonstersRemaining => _gc.IngameState?.Data?.ServerData?.MonstersRemaining ?? 0;

    public System.Numerics.Vector2 PlayerGridPos =>
        _gc.Player?.GridPosNum ?? System.Numerics.Vector2.Zero;

    public System.Numerics.Vector3 PlayerWorldPos =>
        _gc.Player?.PosNum ?? System.Numerics.Vector3.Zero;

    public float PlayerZ => _gc.Player?.PosNum.Z ?? 0f;

    public IReadOnlyList<Entity> FindEntitiesByPath(string pathFragment)
    {
        return _gc.EntityListWrapper.Entities
            .Where(e => e?.Path != null && e.Path.Contains(pathFragment))
            .ToList();
    }

    /// <summary>
    /// All valid town portal entities in the current area.
    /// </summary>
    public IReadOnlyList<Entity> GetTownPortals()
    {
        if (!IsReady) return [];
        return _gc.EntityListWrapper.ValidEntitiesByType
            .GetValueOrDefault(EntityType.TownPortal)?
            .ToList() ?? [];
    }

    /// <summary>
    /// Portals leading to the given farm, matched STRICTLY by destination name
    /// (Render.Name is the destination area). No positional fallback — portals to
    /// other areas standing at the map device must never be adopted or entered.
    /// </summary>
    public IReadOnlyList<Entity> FindFarmPortals(Func<string, bool> isFarmArea)
    {
        return GetTownPortals()
            .Where(e =>
            {
                var renderName = e.GetComponent<Render>()?.Name;
                return renderName != null && isFarmArea(renderName);
            })
            .ToList();
    }

    /// <summary>
    /// The hideout map device entity (e.g. Metadata/Terrain/Missions/Hideouts/Objects/StrDexIntMappingDevice).
    /// </summary>
    public Entity? FindMapDevice()
    {
        if (!IsReady) return null;
        return _gc.EntityListWrapper.Entities
            .Where(e => e?.Path != null && e.Path.Contains(MapDevicePathFragment))
            .OrderBy(e => System.Numerics.Vector2.Distance(PlayerGridPos, e.GridPosNum))
            .FirstOrDefault();
    }

    /// <summary>
    /// The hideout stash entity (Metadata/MiscellaneousObjects/Stash).
    /// </summary>
    public Entity? FindStash()
    {
        if (!IsReady) return null;
        var byType = _gc.EntityListWrapper.ValidEntitiesByType
            .GetValueOrDefault(EntityType.Stash)?
            .OrderBy(e => System.Numerics.Vector2.Distance(PlayerGridPos, e.GridPosNum))
            .FirstOrDefault();
        if (byType != null) return byType;

        return _gc.EntityListWrapper.Entities
            .Where(e => e?.Path != null && e.Path.Contains(StashPathFragment))
            .OrderBy(e => System.Numerics.Vector2.Distance(PlayerGridPos, e.GridPosNum))
            .FirstOrDefault();
    }

    /// <summary>
    /// Alive, hostile, targetable monsters. No distance filter by default — the
    /// entity list is already bounded by the server's network bubble, which is the
    /// natural "what we can react to" range. Targetable-only filters out invisible
    /// helper daemons (e.g. the Simulacrum pack-spawner daemons), which would
    /// otherwise be hovered as "monsters". Hidden (unspawned) pack members are
    /// deliberately INCLUDED — walking to them activates their pack; the near-first
    /// band and target time-stick bound how far they can pull the bot.
    /// </summary>
    public IReadOnlyList<Entity> FindNearbyMonsters(float gridRange = 0)
    {
        if (!IsReady) return [];
        var playerPos = PlayerGridPos;
        return _gc.EntityListWrapper.ValidEntitiesByType
            .GetValueOrDefault(EntityType.Monster)?
            .Where(e => e.IsAlive && e.IsHostile && e.IsTargetable &&
                        (gridRange <= 0 ||
                         System.Numerics.Vector2.Distance(playerPos, e.GridPosNum) <= gridRange))
            .ToList() ?? [];
    }

    /// <summary>
    /// Dump-eligible stash tab names in display order (readable without the stash
    /// panel open; PlayerStashTabs is server-ordered, so sort by VisibleIndex).
    /// The raw list also contains the map stash's internal sub-stashes (one per
    /// section, ALL named "1", type Map), folders and special tabs -- only
    /// Normal/Premium/Quad tabs accept arbitrary loot, so only those are offered.
    /// </summary>
    public IReadOnlyList<string> GetStashTabNames() =>
        SafeRead.Try<IReadOnlyList<string>>("gs.stash-tabs", () =>
        {
            var tabs = _gc.IngameState?.Data?.ServerData?.PlayerStashTabs;
            if (tabs == null) return [];
            return tabs
                .Where(t => !string.IsNullOrEmpty(t?.Name))
                .Where(t => t.TabType is InventoryTabType.Normal
                    or InventoryTabType.Premium
                    or InventoryTabType.Quad)
                .Where(t => !t.Flags.HasFlag(InventoryTabFlags.RemoveOnly))
                .OrderBy(t => t.VisibleIndex)
                .Select(t => t.Name)
                .ToList();
        }) ?? [];

    /// <summary>Tiles below this targeting-layer value block sight. Chosen strict:
    /// unnecessary walking is harmless, hovering through a wall is not.</summary>
    private const int ClearSightThreshold = 2;

    /// <summary>
    /// Line of sight between two grid points over the terrain TARGETING layer
    /// (RawTerrainTargetingData — what blocks projectiles/vision, distinct from
    /// walkability). Bresenham walk; endpoints themselves are not checked so a
    /// monster standing on a low-value doodad tile doesn't read as unsighted.
    /// Returns true when no terrain data is available (never block on missing data).
    /// </summary>
    public bool HasLineOfSight(System.Numerics.Vector2 from, System.Numerics.Vector2 to)
    {
        var grid = SafeRead.Try("gs.terrain-targeting",
            () => _gc.IngameState?.Data?.RawTerrainTargetingData);
        if (grid == null || grid.Length == 0) return true;

        int x0 = (int)from.X, y0 = (int)from.Y;
        int x1 = (int)to.X, y1 = (int)to.Y;
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        var err = dx - dy;
        var first = true;

        while (true)
        {
            var atEnd = x0 == x1 && y0 == y1;
            if (!first && !atEnd)
            {
                if (y0 < 0 || y0 >= grid.Length) return false;
                var row = grid[y0];
                if (row == null || x0 < 0 || x0 >= row.Length) return false;
                if (row[x0] < ClearSightThreshold) return false;
            }

            if (atEnd) return true;
            first = false;

            var e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    public RectangleF WindowRect => _gc.Window.GetWindowRectangle();

    public System.Numerics.Vector2 WorldToScreen(System.Numerics.Vector3 worldPos)
    {
        if (_gc.IngameState?.Camera == null) return System.Numerics.Vector2.Zero;
        return _gc.IngameState.Camera.WorldToScreen(worldPos);
    }

    /// <summary>
    /// Convert a window-relative UI point to an absolute screen position for clicking.
    /// </summary>
    public System.Numerics.Vector2 UiPointToScreen(System.Numerics.Vector2 windowRelative)
    {
        var rect = WindowRect;
        return new System.Numerics.Vector2(windowRelative.X + rect.Left, windowRelative.Y + rect.Top);
    }

    public float[][]? RawTerrainHeightData => IsReady ? _gc.IngameState.Data.RawTerrainHeightData : null;
}
