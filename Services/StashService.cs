using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared;

namespace NightmareAutomate.Services;

public enum PullResult
{
    /// <summary>An item was moved into the player inventory.</summary>
    Pulled,

    /// <summary>The tab opened fine but holds no matching item — genuinely out of stock.</summary>
    Empty,

    /// <summary>The stash/tab/click flow itself failed — says nothing about stock.</summary>
    Failed
}

/// <summary>
/// Self-contained stashing — no Stashie dependency.
///
/// Dumps the whole inventory into the
/// user's dump tabs, in order. The game's own stash affinities do the sorting:
/// ctrl-clicked items with an affinity go to their affinity tab regardless of the
/// visible tab; everything else lands in the currently open dump tab. When an item
/// refuses to leave the inventory (tab full), we switch to the next dump tab.
/// </summary>
public sealed class StashService
{
    private readonly GameStateService _gameState;
    private readonly InventoryService _inventory;
    private readonly NavigationService _navigation;
    private readonly InputService _input;
    private readonly Action<string> _log;
    private readonly Func<string> _getDumpTabNames;

    public StashService(
        GameStateService gameState,
        InventoryService inventory,
        NavigationService navigation,
        InputService input,
        Action<string> log,
        Func<string> getDumpTabNames)
    {
        _gameState = gameState;
        _inventory = inventory;
        _navigation = navigation;
        _input = input;
        _log = log;
        _getDumpTabNames = getDumpTabNames;
    }

    private StashElement? Stash => _gameState.Controller.IngameState?.IngameUi?.StashElement;

    public bool IsStashPanelOpen => Stash?.IsVisible == true;

    /// <summary>The currently visible stash tab's UI inventory (item elements with
    /// clickable rects). Null while the panel is closed.</summary>
    public ExileCore.PoEMemory.MemoryObjects.Inventory? VisibleStashInventory => Stash?.VisibleStash;

    private IReadOnlyList<string> DumpTabNames =>
        _getDumpTabNames().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Emergency valve only: the normal deposit trigger is event-driven — looting
    /// hits an item that doesn't fit → in-area stash → re-loot. This hideout check
    /// only fires when that path left the inventory completely full anyway
    /// (in-area stash failed or was disabled).
    /// </summary>
    public bool ShouldStash()
    {
        return _inventory.FreeMainInventoryCells() == 0 &&
               _inventory.GetDumpableItems().Count > 0;
    }

    /// <summary>
    /// Full deposit flow: walk to stash, open it, dump into the configured tabs in
    /// order (next tab when one fills up), close. keepOpen leaves the panel up so
    /// the caller can run more stash work (fragment restock) in the
    /// same session.
    /// </summary>
    public async SyncTask<bool> Execute(CancellationToken ct, bool keepOpen = false)
    {
        if (_inventory.GetDumpableItems().Count == 0)
            return true;

        if (!await OpenStashPanel(ct))
            return false;

        var tabNames = DumpTabNames;
        if (tabNames.Count == 0)
        {
            _log("[Stash] No dump tabs configured -- set 'Dump Tab Names' in the settings.");
            return false;
        }

        var dumpedEverything = false;
        foreach (var tabName in tabNames)
        {
            if (ct.IsCancellationRequested) break;

            var tabIndex = GetTabIndex(tabName);
            if (tabIndex < 0)
            {
                _log($"[Stash] Dump tab '{tabName}' not found -- check the name.");
                continue;
            }

            if (!await SwitchToTab(tabIndex, ct))
            {
                _log($"[Stash] Could not switch to tab '{tabName}'.");
                continue;
            }

            await DumpIntoCurrentTab(ct);

            if (_inventory.GetDumpableItems().Count == 0)
            {
                dumpedEverything = true;
                break;
            }

            _log($"[Stash] Tab '{tabName}' is full (or items bounced) -- trying the next dump tab.");
        }

        if (!dumpedEverything)
        {
            var left = _inventory.GetDumpableItems().Count;
            if (left > 0)
                _log($"[Stash] {left} items could not be deposited -- all dump tabs full?");
        }

        if (!keepOpen)
            await ClosePanel(ct);
        return dumpedEverything || _inventory.GetDumpableItems().Count == 0;
    }

    /// <summary>
    /// Close the stash panel (no-op when already closed).
    /// </summary>
    public async SyncTask<bool> ClosePanel(CancellationToken ct)
    {
        if (!IsStashPanelOpen) return true;
        await _input.PressKey(Keys.Escape, ct);
        await Task.Delay(300, ct);
        return !IsStashPanelOpen;
    }

    /// <summary>
    /// Switch the open stash panel to a tab by name. Extra settle delay after the
    /// switch: the tab's item list streams in a moment after IndexVisibleStash flips.
    /// </summary>
    public async SyncTask<bool> SwitchToTabByName(string tabName, CancellationToken ct)
    {
        var tabIndex = GetTabIndex(tabName);
        if (tabIndex < 0)
        {
            _log($"[Stash] Tab '{tabName}' not found -- check the name.");
            return false;
        }

        if (!await SwitchToTab(tabIndex, ct))
        {
            _log($"[Stash] Could not switch to tab '{tabName}'.");
            return false;
        }

        await Task.Delay(300, ct);
        return true;
    }

    /// <summary>
    /// Pull ONE item matching the path fragment from the given tab into the player
    /// inventory (ctrl-click), verified via the inventory change counter. Opens the
    /// stash panel if needed and leaves it open (session-style, like Execute with
    /// keepOpen). Empty ONLY when the tab was reached and holds no such item —
    /// flow failures (panel, tab switch, clicks) report Failed so callers can't
    /// mistake them for "out of stock".
    /// </summary>
    public async SyncTask<PullResult> PullItemFromTab(string tabName, string itemPathFragment, CancellationToken ct)
    {
        if (!await OpenStashPanel(ct)) return PullResult.Failed;
        if (!await SwitchToTabByName(tabName, ct)) return PullResult.Failed;

        for (var attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
        {
            var item = FindVisibleTabItem(itemPathFragment);
            if (item == null)
            {
                // Tab items can stream in a beat late right after the switch.
                if (attempt == 0)
                {
                    await Task.Delay(500, ct);
                    continue;
                }

                _log($"[Stash] No '{itemPathFragment}' in tab '{tabName}'.");
                return PullResult.Empty;
            }

            var counterBefore = InventoryCounter;
            await _input.CtrlClickRect(item.GetClientRect(), ct);
            if (await WaitForInventoryChange(counterBefore, 2000, ct))
                return PullResult.Pulled;

            _log("[Stash] Ctrl-click did not move the item -- retrying.");
        }

        return PullResult.Failed;
    }

    /// <summary>
    /// First item element in the visible tab whose entity path matches.
    /// </summary>
    private ExileCore.PoEMemory.Elements.InventoryElements.NormalInventoryItem? FindVisibleTabItem(
        string itemPathFragment)
    {
        try
        {
            return VisibleStashInventory?.VisibleInventoryItems?
                .FirstOrDefault(i => i?.Item?.Path != null && i.Item.Path.Contains(itemPathFragment));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The main inventory's server-side change counter — bumps whenever the
    /// inventory content changes, i.e. an item actually moved to the stash.
    /// </summary>
    private int InventoryCounter => _inventory.MainInventory?.ServerRequestCounter ?? -1;

    private SyncTask<bool> WaitForInventoryChange(int counterBefore, int timeoutMs, CancellationToken ct) =>
        Helpers.WaitHelpers.For(() => InventoryCounter != counterBefore, timeoutMs, ct);

    /// <summary>
    /// Ctrl-click every dumpable item into the open tab. Holds Ctrl once for the
    /// whole batch under a single input controller. Each click is verified via the
    /// inventory's ServerRequestCounter (any change = the item moved); a click that
    /// didn't take is retried once as shift+ctrl-click, then the item is skipped.
    /// Stops early when items stop leaving the inventory (tab full).
    /// </summary>
    private async SyncTask<bool> DumpIntoCurrentTab(CancellationToken ct)
    {
        using var controller = _input.AcquireController();
        if (controller == null)
        {
            _log("[Stash] Could not acquire the input controller.");
            return false;
        }

        var consecutiveFailures = 0;
        var skipped = new HashSet<long>();
        await controller.KeyDown(Keys.LControlKey, ct);
        try
        {
            while (!ct.IsCancellationRequested && consecutiveFailures < 2)
            {
                var items = _inventory.GetDumpableItems()
                    .Where(i => !skipped.Contains(i.Item?.Address ?? 0))
                    .ToList();
                if (items.Count == 0) break;

                var item = items[0];
                var clickPos = _input.RandomScreenPoint(item.GetClientRect());

                await controller.MoveMouse(clickPos, ct);

                var counterBefore = InventoryCounter;
                await controller.Click(MouseButtons.Left, ct);
                var moved = await WaitForInventoryChange(counterBefore, 1500, ct);

                if (!moved)
                {
                    // Some items refuse a plain ctrl-click — retry as shift+ctrl.
                    _log("[Stash] Ctrl-click did not move the item -- trying shift+ctrl-click.");
                    await controller.KeyDown(Keys.LShiftKey, ct);
                    try
                    {
                        await controller.Click(MouseButtons.Left, ct);
                    }
                    finally
                    {
                        await controller.KeyUp(Keys.LShiftKey, false, ct);
                    }

                    moved = await WaitForInventoryChange(counterBefore, 1500, ct);
                }

                if (!moved)
                {
                    _log($"[Stash] Item refuses to move ({item.Item?.Path ?? "?"}) -- skipping it.");
                    skipped.Add(item.Item?.Address ?? 0);
                }

                consecutiveFailures = moved ? 0 : consecutiveFailures + 1;
            }
        }
        finally
        {
            await controller.KeyUp(Keys.LControlKey, false, ct);
        }

        // Two items in a row refused to move — treat the tab as full.
        return _inventory.GetDumpableItems().Count == 0;
    }

    /// <summary>
    /// Tab index by name (case-insensitive) from the stash panel's tab list.
    /// </summary>
    private int GetTabIndex(string tabName)
    {
        var inventories = Stash?.Inventories;
        if (inventories == null) return -1;

        for (var i = 0; i < inventories.Count; i++)
        {
            if (string.Equals(inventories[i]?.TabName, tabName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Switch tabs with left/right arrow presses, verifying via IndexVisibleStash.
    /// </summary>
    private async SyncTask<bool> SwitchToTab(int tabIndex, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
        {
            var current = Stash?.IndexVisibleStash ?? -1;
            if (current == tabIndex) return true;
            if (current < 0) return false;

            var delta = tabIndex - current;
            var key = delta < 0 ? Keys.Left : Keys.Right;
            for (var i = 0; i < Math.Abs(delta) && !ct.IsCancellationRequested; i++)
            {
                await _input.PressKey(key, ct);
                await Task.Delay(200, ct);
            }

            await Task.Delay(250, ct);
        }

        return Stash?.IndexVisibleStash == tabIndex;
    }

    public async SyncTask<bool> OpenStashPanel(CancellationToken ct)
    {
        // Right after an area transition (chores start the moment we land back in
        // the hideout) the entity list is still streaming — the stash appears a
        // beat after IsLoading clears. Wait for it instead of failing instantly.
        if (!IsStashPanelOpen && _gameState.FindStash() == null &&
            !await Helpers.WaitHelpers.For(() => _gameState.FindStash() != null, 5000, ct))
        {
            _log("[Stash] No stash entity found in this area.");
            return false;
        }

        for (var attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
        {
            if (IsStashPanelOpen) return true;

            var stash = _gameState.FindStash();
            if (stash == null)
            {
                _log("[Stash] No stash entity found in this area.");
                return false;
            }

            // ClickEntity walks next to the stash BEFORE clicking (InteractDistance):
            // the Simulacrum wave-modifier panel can draw over the stash from a
            // distance, swallowing the click. It also waits for the stash to
            // highlight (isTargeted) under the cursor before clicking.
            _log($"[Stash] Opening stash (attempt {attempt + 1})...");
            await _navigation.ClickEntity(stash, ct);

            // Generous timeout: a direct click from across the screen auto-walks
            // the character first; the wait exits early the moment the panel opens.
            if (await Helpers.WaitHelpers.For(() => IsStashPanelOpen, 6000, ct))
                return true;
        }

        // Directional fallback: from one side the stash can refuse to highlight
        // (something draws over it from that angle — seen in Simulacrum areas).
        // Approach from north, south, east, west and retry the hover+click from
        // each side before giving up.
        var target = _gameState.FindStash();
        if (target != null)
        {
            Vector2[] offsets =
            [
                new(0, -20), // north
                new(0, 20),  // south
                new(20, 0),  // east
                new(-20, 0)  // west
            ];

            foreach (var offset in offsets)
            {
                if (ct.IsCancellationRequested) break;
                if (IsStashPanelOpen) return true;

                _log("[Stash] Stash won't highlight -- approaching from another side...");
                await _navigation.MoveToTarget(target.GridPosNum + offset, ct, 6);
                await _navigation.ClickEntity(target, ct);
                if (await Helpers.WaitHelpers.For(() => IsStashPanelOpen, 4000, ct))
                    return true;
            }
        }

        _log("[Stash] Failed to open stash panel.");
        return false;
    }
}
