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
    Failed,

    /// <summary>The configured tab does not exist — no retry can fix this, only a
    /// settings change.</summary>
    ConfigError
}

public enum StashResult
{
    /// <summary>Everything dumpable was deposited.</summary>
    Ok,

    /// <summary>A transient flow failure (panel, clicks, full tabs) — retryable.</summary>
    Failed,

    /// <summary>The dump-tab settings are wrong (none configured / none exist) —
    /// no retry can fix this, only a settings change.</summary>
    ConfigError
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

    /// <summary>Human-readable description of the last failure's failing STEP —
    /// surfaced in engine webhooks so a remote user can tell a settings problem
    /// from a transient UI hiccup. Set at every failure site, cleared on success.</summary>
    public string? LastFailure { get; private set; }

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
    /// same session. ConfigError (no dump tabs set / none of them exist) is kept
    /// apart from transient failures — waiting cannot fix a wrong tab name.
    /// </summary>
    public async SyncTask<StashResult> Execute(CancellationToken ct, bool keepOpen = false)
    {
        if (_inventory.GetDumpableItems().Count == 0)
            return StashResult.Ok;

        var tabNames = DumpTabNames;
        if (tabNames.Count == 0)
        {
            LastFailure = "No dump tabs configured -- set 'Dump Tab Names' in the settings.";
            _log($"[Stash] {LastFailure}");
            return StashResult.ConfigError;
        }

        if (!await OpenStashPanel(ct))
            return StashResult.Failed;

        var dumpedEverything = false;
        var anyTabFound = false;
        foreach (var tabName in tabNames)
        {
            if (ct.IsCancellationRequested) break;

            var tabIndex = GetTabIndex(tabName);
            if (tabIndex < 0)
            {
                _log($"[Stash] Dump tab '{tabName}' not found -- check the name.");
                continue;
            }

            anyTabFound = true;
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

        if (!keepOpen)
            await ClosePanel(ct);

        if (dumpedEverything || _inventory.GetDumpableItems().Count == 0)
        {
            LastFailure = null;
            return StashResult.Ok;
        }

        if (!anyTabFound)
        {
            LastFailure = $"None of the dump tabs ('{string.Join("', '", tabNames)}') exist in the stash -- check 'Dump Tab Names'.";
            _log($"[Stash] {LastFailure}");
            return StashResult.ConfigError;
        }

        var left = _inventory.GetDumpableItems().Count;
        LastFailure = $"{left} items could not be deposited -- all dump tabs full?";
        _log($"[Stash] {LastFailure}");
        return StashResult.Failed;
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

        // A missing tab is a settings problem, not a flow failure — the caller
        // must not burn retries (or backoffs) on something waiting cannot fix.
        if (GetTabIndex(tabName) < 0)
        {
            LastFailure = $"Restock tab '{tabName}' not found in the stash -- check the tab name setting.";
            _log($"[Stash] {LastFailure}");
            return PullResult.ConfigError;
        }

        if (!await SwitchToTabByName(tabName, ct))
        {
            LastFailure = $"Could not switch to tab '{tabName}'.";
            return PullResult.Failed;
        }

        for (var attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
        {
            // The panel closing mid-session must NOT read as "tab empty": an
            // Empty verdict means "out of fragments" to the caller and would
            // end the whole farming session on a UI hiccup.
            if (!IsStashInteractable)
            {
                LastFailure = "The stash panel closed (or got covered) mid-restock.";
                _log($"[Stash] {LastFailure}");
                return PullResult.Failed;
            }

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
            {
                LastFailure = null;
                return PullResult.Pulled;
            }

            _log("[Stash] Ctrl-click did not move the item -- retrying.");
            // Clear anything that may have opened over the stash, and rule out
            // stale host reads (wrong item rects) before the retry.
            await CloseBlockingPanel(ct);
            if (attempt >= 1 && _gameState.TryForceRefreshArea())
            {
                _log("[Stash] Forced an area refresh to rule out stale reads.");
                await Task.Delay(1000, ct);
            }
        }

        LastFailure = $"Ctrl-clicking the item out of tab '{tabName}' never moved it.";
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

    /// <summary>Whether stash items can actually be clicked. Atlas and stash are
    /// mutually exclusive panels, so with the Atlas up the stash cannot be open —
    /// the extra check only guards transition frames where both could briefly
    /// read visible.</summary>
    private bool IsStashInteractable => IsStashPanelOpen && !_gameState.IsAtlasOpen;

    /// <summary>
    /// Clear whatever panel would swallow the stash click. The ATLAS is checked
    /// first (e.g. left open by a failed map-device flow): Escaping it closes
    /// the whole Atlas UI, after which the stash entity must be clicked to open
    /// the stash panel fresh — the caller's open loop does that. Beyond that:
    /// the Simulacrum wave-reward/modifier popup is the usual culprit. Escape is
    /// only pressed while something is actually open — with nothing to close it
    /// brings up the game menu, which would then swallow the click itself.
    /// </summary>
    private async SyncTask<bool> CloseBlockingPanel(CancellationToken ct)
    {
        if (_gameState.IsAtlasOpen)
        {
            _log("[Stash] The Atlas is open -- pressing Escape (the stash gets reopened after).");
            await _input.PressKey(Keys.Escape, ct);
            // Longer settle than a normal panel close: the whole Atlas UI tears
            // down and the panel-state reads must not race it.
            await Task.Delay(500, ct);
            return true;
        }

        if (IsStashPanelOpen || !_gameState.HasBlockingUiPanel) return false;
        _log("[Stash] A panel is in the way -- pressing Escape to close it.");
        await _input.PressKey(Keys.Escape, ct);
        await Task.Delay(300, ct);
        return true;
    }

    public async SyncTask<bool> OpenStashPanel(CancellationToken ct)
    {
        // Right after an area transition (chores start the moment we land back in
        // the hideout) the entity list is still streaming — the stash appears a
        // beat after IsLoading clears. Wait for it instead of failing instantly;
        // if it still is not there, force the host to re-read the area (a missed
        // area change leaves searches running against a stale world) and give it
        // one more short wait before giving up.
        if (!IsStashPanelOpen && _gameState.FindStash() == null &&
            !await Helpers.WaitHelpers.For(() => _gameState.FindStash() != null, 5000, ct))
        {
            if (_gameState.TryForceRefreshArea())
            {
                _log("[Stash] No stash entity found -- forced an area refresh to rule out a stale host.");
                await Task.Delay(1000, ct);
                await Helpers.WaitHelpers.For(() => _gameState.FindStash() != null, 3000, ct);
            }

            if (_gameState.FindStash() == null)
            {
                LastFailure = "No stash entity found in this area.";
                _log($"[Stash] {LastFailure}");
                return false;
            }
        }

        for (var attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
        {
            // Blocking panels first: the Atlas can sit OVER an already-open
            // stash panel (both report visible), so "stash open" may only be
            // trusted after the blockers are gone.
            await CloseBlockingPanel(ct);
            if (IsStashInteractable) return true;

            var stash = _gameState.FindStash();
            if (stash == null)
            {
                LastFailure = "No stash entity found in this area.";
                _log($"[Stash] {LastFailure}");
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
            if (await Helpers.WaitHelpers.For(() => IsStashInteractable, 6000, ct))
                return true;
        }

        // Three direct attempts down: before the directional fallback, rule out
        // the host reading stale state (entity positions, panel flags) — the
        // refresh rebuilds its caches; the fallback then works on fresh data.
        if (_gameState.TryForceRefreshArea())
        {
            _log("[Stash] Direct attempts failed -- forced an area refresh before the fallback.");
            await Task.Delay(1000, ct);
            if (IsStashInteractable) return true;
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
                await CloseBlockingPanel(ct);
                if (IsStashInteractable) return true;
                _log("[Stash] Stash won't highlight -- approaching from another side...");
                await _navigation.MoveToTarget(target.GridPosNum + offset, ct, 6);
                await _navigation.ClickEntity(target, ct);
                if (await Helpers.WaitHelpers.For(() => IsStashInteractable, 4000, ct))
                    return true;
            }
        }

        LastFailure = "The stash panel did not open after 3 direct clicks and 4 approach angles.";
        _log("[Stash] Failed to open stash panel.");
        return false;
    }
}
