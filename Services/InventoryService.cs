using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace NightmareAutomate.Services;

/// <summary>
/// Server-side inventory access: player inventory, map device map slot, and map device storage.
/// Server inventories are matched by <see cref="InventorySlotE"/> value — never by list index,
/// which is not stable (some slots are absent until their UI is opened).
/// </summary>
public sealed class InventoryService(GameController gc)
{
    private readonly GameController _gc = gc;

    /// <summary>The map device's single map/fragment slot.</summary>
    public const InventorySlotE MapDeviceMapSlot = InventorySlotE.Map1;

    /// <summary>The map device storage ("mini stash", 4x5 grid). Present in
    /// PlayerInventories at an arbitrary index — always resolve by slot value.</summary>
    public const InventorySlotE MapDeviceStorageSlot = (InventorySlotE)62; // MapDeviceStorage1

    public ServerInventory? GetInventoryBySlot(InventorySlotE slot)
    {
        var inventories = _gc.IngameState?.Data?.ServerData?.PlayerInventories;
        if (inventories == null) return null;

        // Fast path: for low slots the list index usually matches the slot id.
        var idx = (int)slot;
        if (idx >= 0 && idx < inventories.Count)
        {
            var candidate = inventories[idx].Inventory;
            if (candidate != null && candidate.InventSlot == slot)
                return candidate;
        }

        return inventories
            .Select(h => h.Inventory)
            .FirstOrDefault(inv => inv != null && inv.InventSlot == slot);
    }

    public ServerInventory? MainInventory => GetInventoryBySlot(InventorySlotE.MainInventory1);
    public ServerInventory? MapDeviceMapInventory => GetInventoryBySlot(MapDeviceMapSlot);
    public ServerInventory? MapDeviceStorage => GetInventoryBySlot(MapDeviceStorageSlot);

    public bool IsInventoryPanelOpen =>
        _gc.IngameState?.IngameUi?.InventoryPanel?.IsVisible == true;

    /// <summary>
    /// Find an item in the given server inventory by metadata path fragment.
    /// </summary>
    public ServerInventory.InventSlotItem? FindItem(ServerInventory? inventory, string pathFragment)
    {
        return inventory?.InventorySlotItems?
            .FirstOrDefault(s => s.Item?.Path != null && s.Item.Path.Contains(pathFragment));
    }

    public ServerInventory.InventSlotItem? FindMainInventoryItem(string pathFragment) =>
        FindItem(MainInventory, pathFragment);

    public int CountItems(ServerInventory? inventory, string pathFragment)
    {
        return inventory?.InventorySlotItems?
            .Count(s => s.Item?.Path != null && s.Item.Path.Contains(pathFragment)) ?? 0;
    }

    /// <summary>
    /// Whether a ground item would fit into the main inventory: either by merging
    /// into an existing stack of the same base, or into a free WxH rectangle.
    /// </summary>
    public bool CanFitItem(Entity itemEntity)
    {
        var inv = MainInventory;
        if (inv == null) return false;

        // Stack merge (splinters etc.) works even with zero free cells.
        if (itemEntity.TryGetComponent<Stack>(out var stack) && stack.Info != null)
        {
            var mergeTarget = inv.InventorySlotItems?.Any(s =>
                s.Item?.Path == itemEntity.Path &&
                s.Item.TryGetComponent<Stack>(out var existing) &&
                existing.Info != null &&
                existing.Size + stack.Size <= existing.Info.MaxStackSize) == true;
            if (mergeTarget) return true;
        }

        var baseComp = itemEntity.GetComponent<Base>();
        var w = Math.Max(1, (int)(baseComp?.ItemCellsSizeX ?? 1));
        var h = Math.Max(1, (int)(baseComp?.ItemCellsSizeY ?? 1));
        return CanFitItemSize(w, h);
    }

    /// <summary>
    /// First-fit check for a WxH item over the main inventory occupancy grid.
    /// </summary>
    public bool CanFitItemSize(int width, int height)
    {
        var occupied = BuildOccupancyGrid(out var rows, out var cols);
        if (occupied == null) return false;

        for (var y = 0; y <= rows - height; y++)
        {
            for (var x = 0; x <= cols - width; x++)
            {
                var blocked = false;
                for (var dy = 0; dy < height && !blocked; dy++)
                    for (var dx = 0; dx < width && !blocked; dx++)
                        blocked = occupied[y + dy, x + dx];
                if (!blocked) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Inventory items eligible for stash dumping — everything in the main inventory.
    /// </summary>
    public IReadOnlyList<ServerInventory.InventSlotItem> GetDumpableItems()
    {
        return MainInventory?.InventorySlotItems?
            .Where(s => s.Item?.Path != null)
            .OrderBy(s => s.PosY).ThenBy(s => s.PosX)
            .ToList() ?? [];
    }

    /// <summary>
    /// Free cells in the main inventory (occupancy grid over Rows x Columns).
    /// </summary>
    public int FreeMainInventoryCells()
    {
        var occupied = BuildOccupancyGrid(out var rows, out var cols);
        if (occupied == null) return 0;

        var free = 0;
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++)
                if (!occupied[y, x])
                    free++;
        return free;
    }

    private bool[,]? BuildOccupancyGrid(out int rows, out int cols)
    {
        rows = 0;
        cols = 0;
        var inv = MainInventory;
        if (inv?.InventorySlotItems is not { } items) return null;

        rows = inv.Rows;
        cols = inv.Columns;
        if (rows <= 0 || cols <= 0) return null;

        var occupied = new bool[rows, cols];
        foreach (var item in items)
        {
            var endX = Math.Min(cols, item.PosX + item.SizeX);
            var endY = Math.Min(rows, item.PosY + item.SizeY);
            for (var y = Math.Max(0, item.PosY); y < endY; y++)
                for (var x = Math.Max(0, item.PosX); x < endX; x++)
                    occupied[y, x] = true;
        }

        return occupied;
    }
}
