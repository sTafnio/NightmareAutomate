using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
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
    public bool IsAtLoginScreen
    {
        get
        {
            try
            {
                var game = _gc.Game;
                return game.IsLoginState || game.IsSelectCharacterState;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>An InGameState is active on the game-state machine — the relogin
    /// loop's exit signal (same condition the proven Automate relogin uses).
    /// True before the world is fully streamed in; gate actions on IsReady.</summary>
    public bool IsInGameState
    {
        get
        {
            try { return _gc.Game.IsInGameState; }
            catch { return false; }
        }
    }

    /// <summary>An area-loading screen is active on the game-state machine
    /// (login -> world, or between areas). Distinct from IsLoading, which is the
    /// host's cached view and unreliable outside a session.</summary>
    public bool IsLoadingState
    {
        get
        {
            try { return _gc.Game.IsLoadingState; }
            catch { return false; }
        }
    }

    /// <summary>Whether the game window has OS focus — inputs only reach the game
    /// then (relevant for login-screen recovery, where nothing gates on InGame).</summary>
    public bool IsWindowForeground
    {
        get
        {
            try { return _gc.Window.IsForeground(); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Whether a click-swallowing UI panel is open: any visible fullscreen/large
    /// panel the game tracks, or the generic popup window (wave-reward/modifier
    /// dialogs). Used to decide whether pressing Escape would CLOSE something —
    /// Escape with nothing open brings up the game menu instead.
    /// </summary>
    public bool HasBlockingUiPanel
    {
        get
        {
            try
            {
                var ui = _gc.IngameState?.IngameUi;
                if (ui == null) return false;
                if (ui.PopUpWindow?.IsVisible == true) return true;
                if (ui.FullscreenPanels?.Any(p => p?.IsVisible == true) == true) return true;
                if (ui.LargePanels?.Any(p => p?.IsVisible == true) == true) return true;
                return false;
            }
            catch
            {
                return false;
            }
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
    public IReadOnlyList<string> GetStashTabNames()
    {
        try
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
        }
        catch
        {
            return [];
        }
    }

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
        int[][]? grid;
        try
        {
            grid = _gc.IngameState?.Data?.RawTerrainTargetingData;
        }
        catch
        {
            return true;
        }

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
