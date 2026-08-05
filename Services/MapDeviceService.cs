using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;

namespace NightmareAutomate.Services;

public enum MapDeviceResult
{
    Success,
    NoFragments,
    DeviceNotFound,
    Failed
}

/// <summary>
/// Drives the map device, which in this game version is embedded in the Atlas panel:
/// clicking the hideout device opens the Atlas; the "mini stash" (map device storage)
/// is a panel inside it. Right-clicking a fragment in the storage opens the
/// MapDeviceWindow, ctrl-click moves the fragment into the map slot, then ActivateButton.
///
/// All UI discovery is predicate-based (element Entity path + geometry) — no child indices.
/// Fragment sources in priority order: map slot (already loaded) → device storage → player inventory.
///
/// Every UI step runs as a RetryStep: act → frame-checked verify → on failure log a
/// full UI-state snapshot (which panel is open, where the fragment currently is) so a
/// stuck run says WHY it is stuck, then retry with freshly re-found elements.
/// </summary>
public sealed class MapDeviceService
{
    private readonly GameStateService _gameState;
    private readonly InventoryService _inventory;
    private readonly NavigationService _navigation;
    private readonly InputService _input;
    private readonly ExplorationService _exploration;
    private readonly Action<string> _log;
    private readonly Func<Keys> _getInventoryKey;

    public MapDeviceService(
        GameStateService gameState,
        InventoryService inventory,
        NavigationService navigation,
        InputService input,
        ExplorationService exploration,
        Action<string> log,
        Func<Keys> getInventoryKey)
    {
        _gameState = gameState;
        _inventory = inventory;
        _navigation = navigation;
        _input = input;
        _exploration = exploration;
        _log = log;
        _getInventoryKey = getInventoryKey;
    }

    /// <summary>Where the device was last seen, per hideout INSTANCE (area hash).
    /// Hideout entities stream out with distance in large hideouts: a character
    /// dropped at the hideout SPAWN (checkpoint resurrect, /hideout from town)
    /// may not have the device in its entity bubble at all. STATIC so a bot
    /// restart in the same hideout keeps it; never persisted to disk.</summary>
    private static uint _deviceAreaHash;
    private static System.Numerics.Vector2? _devicePointCache;

    private System.Numerics.Vector2? LastKnownDevicePoint
    {
        get { EnsureDevicePointIsForCurrentInstance(); return _devicePointCache; }
        set { EnsureDevicePointIsForCurrentInstance(); _devicePointCache = value; }
    }

    private void EnsureDevicePointIsForCurrentInstance()
    {
        // Only a DIFFERENT hideout instance invalidates — map runs in between
        // must not wipe the point.
        if (!_gameState.IsInHideout) return;
        var hash = _gameState.AreaHash;
        if (hash == 0 || hash == _deviceAreaHash) return;
        _deviceAreaHash = hash;
        _devicePointCache = null;
    }

    private Element? AtlasRoot => _gameState.Controller.IngameState?.IngameUi?.Atlas;

    private MapDeviceWindow? DeviceWindow =>
        _gameState.Controller.IngameState?.IngameUi?.MapDeviceWindow;

    public bool IsAtlasOpen => AtlasRoot?.IsVisible == true;
    public bool IsDeviceWindowOpen => DeviceWindow?.IsVisible == true;

    /// <summary>Human-readable description of the last failure's failing STEP —
    /// surfaced in engine webhooks/logs so a stuck flow says WHY remotely.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Best-effort UI reset between failed flows: the inventory panel is closed
    /// via its own key, then Escape clears whatever else is in the way (Atlas,
    /// device window, reward popups) — the next attempt starts from a clean
    /// screen instead of clicking into a stale half-open UI. Escape is only
    /// pressed while something is actually open (with nothing to close it brings
    /// up the game menu).
    /// </summary>
    public async SyncTask<bool> ResetUi(CancellationToken ct)
    {
        if (_inventory.IsInventoryPanelOpen)
        {
            await _input.PressKey(_getInventoryKey(), ct);
            await WaitFor(() => !_inventory.IsInventoryPanelOpen, 1000, ct);
        }

        // A LOADED map slot with its window still open is the one UI state worth
        // KEEPING between attempts: the retry can click Activate directly, while
        // closing it strands the fragment in the slot — reopening the window
        // needs a spare fragment to right-click, and without one the flow is
        // dead in the water. Only clear popups sitting above it.
        if (IsDeviceWindowOpen && IsMapSlotLoaded)
        {
            for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
            {
                if (!_gameState.HasPopupOpen) return true;
                await _input.PressKey(Keys.Escape, ct);
                await Task.Delay(300, ct);
                if (!IsDeviceWindowOpen) break; // Escape took the window instead — full reset below
            }
            if (IsDeviceWindowOpen && !_gameState.HasPopupOpen) return true;
        }

        for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            if (!IsAtlasOpen && !_gameState.HasBlockingUiPanel)
                return true;
            await _input.PressKey(Keys.Escape, ct);
            await Task.Delay(300, ct);
        }

        return !IsAtlasOpen && !_gameState.HasBlockingUiPanel;
    }

    /// <summary>
    /// Close EVERYTHING — including a loaded device window that ResetUi would
    /// deliberately keep — so the whole-flow retry starts from the same clean
    /// screen as a fresh run. Reopening the window afterwards needs a spare
    /// fragment (storage or inventory) to right-click; the restock guarantees
    /// one exists.
    /// </summary>
    public async SyncTask<bool> ForceCloseUi(CancellationToken ct)
    {
        if (_inventory.IsInventoryPanelOpen)
        {
            await _input.PressKey(_getInventoryKey(), ct);
            await WaitFor(() => !_inventory.IsInventoryPanelOpen, 1000, ct);
        }

        for (var i = 0; i < 4 && !ct.IsCancellationRequested; i++)
        {
            if (!IsAtlasOpen && !_gameState.HasBlockingUiPanel)
                return true;
            await _input.PressKey(Keys.Escape, ct);
            await Task.Delay(300, ct);
        }

        return !IsAtlasOpen && !_gameState.HasBlockingUiPanel;
    }

    /// <summary>
    /// Escalated recovery for a REPEATEDLY failing flow — restarts everything the
    /// next attempt depends on, for failures whose cause is not visible in the UI
    /// at all (the server not sending an update, the host reading stale memory):
    /// 1. the normal UI reset;
    /// 2. a forced host area-refresh, rebuilding the entity list and caches;
    /// 3. a walk away from the device (to the stash), so the next attempt
    ///    re-approaches from a different spot — new entity streaming, new camera
    ///    projection, new highlight geometry.
    /// The walk is skipped while the device window holds a loaded map slot: walking
    /// away closes the window, and without a spare fragment it cannot be reopened.
    /// </summary>
    public async SyncTask<bool> HardReset(CancellationToken ct)
    {
        await ResetUi(ct);

        if (_gameState.TryForceRefreshArea())
        {
            _log("[MapDevice] Hard reset: forced an area refresh to rebuild the host's caches.");
            await Task.Delay(1000, ct);
        }

        if (IsDeviceWindowOpen && IsMapSlotLoaded)
        {
            _log("[MapDevice] Hard reset: keeping the loaded device window open (no walk-away).");
            return true;
        }

        var stash = _gameState.FindStash();
        if (stash != null &&
            System.Numerics.Vector2.Distance(_gameState.PlayerGridPos, stash.GridPosNum) > 20)
        {
            _log("[MapDevice] Hard reset: walking to the stash to re-approach the device fresh.");
            await _navigation.MoveToTarget(stash.GridPosNum, ct, NavigationService.InteractDistance);
        }

        return true;
    }

    /// <summary>Server-side read of the device's map slot — valid even with every
    /// panel closed (the game keeps the slot contents between sessions).</summary>
    private bool IsMapSlotLoaded =>
        _inventory.MapDeviceMapInventory?.InventorySlotItems?.Count > 0;

    /// <summary>UI-side device storage — the "mini stash" panel embedded in the
    /// Atlas, exposed typed by ExileCore with per-item clickable rects. The
    /// server-side item counts live in InventoryService; this is only for
    /// clicking and for detecting the panel's collapsed state.</summary>
    private ExileCore.PoEMemory.MemoryObjects.VendorInventory? UiStorage =>
        _gameState.Controller.IngameState?.IngameUi?.Atlas?.MapDeviceStorage;

    /// <summary>Whether storage items can actually be clicked right now: the
    /// panel is visible and shows item elements for the items the server says
    /// are inside. Server says items + UI shows none = the panel is COLLAPSED
    /// (the game remembers that between sessions) — clicks would hit the atlas
    /// behind it.</summary>
    private bool StorageItemsClickable()
    {
        try
        {
            var ui = UiStorage;
            if (ui == null || !ui.IsVisible) return false;
            var serverCount = _inventory.MapDeviceStorage?.InventorySlotItems?.Count ?? 0;
            if (serverCount == 0) return true; // nothing to click either way
            return ui.VisibleInventoryItems?.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Make the storage panel usable: when the server reports items but the UI
    /// shows no item elements, the panel is collapsed — click its expand toggle
    /// (a small button hugging the left screen edge next to the panel) and
    /// verify items appear. If no toggle candidate is found or expanding fails,
    /// the Atlas tree is dumped to the log so the toggle can be identified.
    /// </summary>
    private async SyncTask<bool> EnsureStorageExpanded(CancellationToken ct)
    {
        if (!IsAtlasOpen) return false;
        if (StorageItemsClickable()) return true;

        _log("[MapDevice] The device storage panel looks collapsed -- looking for its expand toggle.");
        for (var attempt = 0; attempt < 2 && !ct.IsCancellationRequested; attempt++)
        {
            var toggle = FindStorageToggleCandidate();
            if (toggle == null) break;

            _log($"[MapDevice] Clicking the storage toggle at {RectDesc(toggle)}.");
            await _input.ClickRect(toggle.GetClientRectCache, MouseButtons.Left, ct);
            await Task.Delay(500, ct);
            if (StorageItemsClickable()) return true;
        }

        if (!StorageItemsClickable())
        {
            _log("[MapDevice] Could not expand the storage panel.");
            DumpAtlasUiTree("storage panel stayed collapsed");
            return false;
        }

        return true;
    }

    /// <summary>Heuristic for the storage expand/collapse toggle: a small leaf
    /// element hugging the LEFT edge of the screen in the storage panel's
    /// vertical band. Only consulted when the panel is collapsed (the toggle is
    /// the only small left-edge element then).</summary>
    private Element? FindStorageToggleCandidate()
    {
        var root = AtlasRoot;
        if (root == null || !root.IsVisible) return null;

        try
        {
            var candidates = new List<Element>();
            root.GetAllElementsRecursive(e =>
            {
                try
                {
                    if (e is not { IsVisible: true } || e.ChildCount > 2) return false;
                    var r = e.GetClientRectCache;
                    return r.Width is >= 10 and <= 70 && r.Height is >= 10 and <= 70 &&
                           r.Center.X < 80 && r.Center.Y is > 60 and < 760;
                }
                catch
                {
                    return false;
                }
            }, candidates);

            return candidates.OrderBy(e => e.GetClientRectCache.Center.Y).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log($"[MapDevice] Storage toggle search failed: {ex.Message}");
            return null;
        }
    }

    private static string RectDesc(Element e)
    {
        var r = e.GetClientRectCache;
        return $"({(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height})";
    }

    /// <summary>
    /// Park an INVENTORY spare fragment into the device storage — done while the
    /// device window is open and the slot is already armed, right before
    /// activation. Ctrl-click with a FULL slot moves the fragment into the
    /// storage (not the slot), which keeps the inventory clean and guarantees
    /// the next "window closed" recovery has a storage fragment to right-click.
    /// Best-effort: a failure is logged, never fatal.
    /// </summary>
    private async SyncTask<bool> StowInventorySpareToStorage(string itemPath, CancellationToken ct)
    {
        if (!IsDeviceWindowOpen || !IsInMapSlot(itemPath)) return true;
        if (_inventory.FindMainInventoryItem(itemPath) == null) return true;

        if (!await EnsureInventoryPanelOpen(ct)) return false;

        var storageBefore = _inventory.CountItems(_inventory.MapDeviceStorage, itemPath);
        _log("[MapDevice] Parking the spare fragment from the inventory into the device storage...");
        await ClickInventoryFragment(itemPath, MouseButtons.Left, ctrl: true, ct);

        var moved = await WaitFor(() =>
            _inventory.CountItems(_inventory.MapDeviceStorage, itemPath) > storageBefore ||
            _inventory.FindMainInventoryItem(itemPath) == null, 2000, ct);
        if (!moved)
            _log($"[MapDevice] The spare did not move into the storage -- leaving it in the inventory. {UiState(itemPath)}");
        return moved;
    }

    /// <summary>
    /// Log the visible Atlas UI tree (index path, rect, child count, plus any
    /// text/texture/entity binding) — fired automatically when a UI recovery
    /// cannot find its element, and available manually from the settings. One
    /// dump identifies where a moved/renamed element went after a game patch.
    /// Containers with 100+ children (the atlas map plane) are summarized, not
    /// descended into.
    /// </summary>
    public void DumpAtlasUiTree(string reason)
    {
        var root = AtlasRoot;
        if (root == null || !root.IsVisible)
        {
            _log($"[MapDevice] UI dump skipped ({reason}): the Atlas is not open.");
            return;
        }

        _log($"[MapDevice] --- Atlas UI dump: {reason} ---");
        var lines = 0;

        void Walk(Element? e, string path, int depth)
        {
            if (e == null || lines >= 150) return;
            try
            {
                if (e.Address == 0 || !e.IsVisible) return;

                var r = e.GetClientRectCache;
                var extras = "";
                try
                {
                    var t = e.Text;
                    if (!string.IsNullOrEmpty(t))
                        extras += $" text='{(t.Length > 30 ? t[..30] : t)}'";
                }
                catch { /* unreadable */ }

                try
                {
                    var tex = e.TextureName;
                    if (!string.IsNullOrEmpty(tex))
                        extras += $" tex='{(tex.Length > 45 ? tex[^45..] : tex)}'";
                }
                catch { /* unreadable */ }

                try
                {
                    var ent = e.Entity?.Path;
                    if (ent != null)
                        extras += $" entity='{ent[(ent.LastIndexOf('/') + 1)..]}'";
                }
                catch { /* unreadable */ }

                // Keep the dump readable: structure rows for shallow depths,
                // detail rows only when they carry something identifying.
                var small = r.Width is >= 8 and <= 90 && r.Height is >= 8 and <= 90;
                if (depth <= 2 || extras.Length > 0 || small)
                {
                    _log($"[UiDump] {path} rect=({(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height}) " +
                         $"kids={e.ChildCount}{extras}");
                    lines++;
                }

                if (depth >= 6) return;
                if (e.ChildCount > 100)
                {
                    _log($"[UiDump] {path} -> {e.ChildCount} children skipped (bulk container).");
                    lines++;
                    return;
                }

                for (var i = 0; i < e.ChildCount && lines < 150; i++)
                    Walk(e.GetChildAtIndex(i), $"{path}->{i}", depth + 1);
            }
            catch { /* element mid-update */ }
        }

        Walk(root, "Atlas", 0);
        _log($"[MapDevice] --- Atlas UI dump end ({lines} rows) ---");
    }

    /// <summary>
    /// Full flow: open device UI, load items into the map slot, activate, wait for portals.
    /// A UI-step failure gets ONE full in-flow restart: everything (Atlas, stash,
    /// inventory) is closed and the whole flow reruns from a clean screen — a step
    /// that failed against a half-open or shifted UI usually works on a fresh pass.
    /// </summary>
    public async SyncTask<MapDeviceResult> PrepareAndActivate(
        IReadOnlyList<string> itemPaths,
        Func<string, bool> isFarmArea,
        CancellationToken ct)
    {
        var result = await PrepareAndActivateOnce(itemPaths, isFarmArea, ct);
        if (result == MapDeviceResult.Failed && !ct.IsCancellationRequested)
        {
            _log($"[MapDevice] Flow failed ({LastFailure ?? "unknown step"}) -- " +
                 "closing all UI and rerunning the whole flow once from scratch.");
            await ForceCloseUi(ct);
            // Rule out stale host reads for the second pass — a step that failed
            // against stale element rects/entities works on rebuilt caches.
            if (_gameState.TryForceRefreshArea())
            {
                _log("[MapDevice] Forced an area refresh before the rerun.");
                await Task.Delay(1000, ct);
            }

            await Task.Delay(1500, ct);
            result = await PrepareAndActivateOnce(itemPaths, isFarmArea, ct);
        }

        return result;
    }

    private async SyncTask<MapDeviceResult> PrepareAndActivateOnce(
        IReadOnlyList<string> itemPaths,
        Func<string, bool> isFarmArea,
        CancellationToken ct)
    {

        // The entity search returns null for reasons that have nothing to do
        // with the device itself: the game is not ready (loading tail, relogin
        // limbo, invalid player) or this is not the hideout at all. Ride out
        // transient states briefly, then report WHICH case it was — a bare
        // "device missing" sends the operator hunting the wrong problem.
        await WaitFor(() => _gameState.IsReady && _gameState.FindMapDevice() != null, 5000, ct);

        // Ready and in the hideout, yet no device: in large hideouts entities
        // stream out with distance, so a character dropped at the hideout SPAWN
        // (checkpoint resurrect, /hideout) simply cannot SEE the device from
        // there. Walk back to where it was last seen — or tour the hideout when
        // no spot is remembered — before concluding anything about the device.
        if (_gameState.FindMapDevice() == null && _gameState.IsReady && _gameState.IsInHideout)
        {
            // The gap may be the HOST's, not distance: a missed area change
            // leaves searches running against a stale world. A forced re-read
            // is cheaper than walking -- rule it out before moving anywhere.
            if (_gameState.TryForceRefreshArea())
            {
                _log("[MapDevice] Entity search came up empty -- forcing an area refresh first.");
                await Task.Delay(1000, ct);
            }

            if (_gameState.FindMapDevice() == null)
            {
                if (LastKnownDevicePoint is { } knownPoint &&
                    System.Numerics.Vector2.Distance(_gameState.PlayerGridPos, knownPoint) > 40)
                {
                    _log("[MapDevice] Device not in the entity bubble -- walking to where it was last seen.");
                    await _navigation.MoveToTarget(knownPoint, ct, 30);
                }
                else if (LastKnownDevicePoint == null)
                {
                    _log("[MapDevice] Device not in the entity bubble and no remembered spot -- touring the hideout.");
                    var deadline = DateTime.Now.AddMinutes(2);
                    await _exploration.ExploreUntil(
                        Helpers.WaitHelpers.Throttle(
                            () => _gameState.FindMapDevice() != null || DateTime.Now > deadline, 250), ct);
                }
            }
        }

        var device = _gameState.FindMapDevice();
        if (device == null)
        {
            var pos = _gameState.PlayerGridPos;
            var why = !_gameState.IsReady
                ? "the game is not ready (loading screen, relogin limbo, or an invalid player object)"
                : !_gameState.IsInHideout
                    ? $"the current area '{_gameState.AreaName}' is not a hideout"
                    : $"no entity path matches in '{_gameState.AreaName}' " +
                      $"({_gameState.Controller.EntityListWrapper.Entities.Count} entities listed, " +
                      $"player at {(int)pos.X},{(int)pos.Y})";
            LastFailure = $"No map device entity found -- {why}.";
            _log($"[MapDevice] {LastFailure}");
            return MapDeviceResult.DeviceNotFound;
        }

        // Remember the spot for the next time the character strands far away.
        LastKnownDevicePoint = device.GridPosNum;

        // A blocking panel (wave reward popup, game menu, ...) would swallow the
        // device click — clear it before the flow starts. The Atlas itself being
        // open already is fine (the open step verifies and skips).
        if (!IsAtlasOpen && _gameState.HasBlockingUiPanel)
        {
            _log("[MapDevice] A panel is in the way -- pressing Escape to close it.");
            await _input.PressKey(Keys.Escape, ct);
            await Task.Delay(300, ct);
        }

        // 6s verify: a direct click from across the screen auto-walks the
        // character first; the wait exits early the moment the Atlas opens.
        if (!await RetryStep("Open map device UI", () => IsAtlasOpen,
                () => _navigation.ClickEntity(device, ct), 6000, ct))
        {
            LastFailure = "The map device UI (Atlas) did not open after 3 clicks.";
            return MapDeviceResult.Failed;
        }

        foreach (var path in itemPaths)
        {
            var loaded = await EnsureItemInMapSlot(path, ct);
            if (loaded != MapDeviceResult.Success)
                return loaded;
        }

        if (!await ActivateAndWaitForPortals(isFarmArea, ct))
        {
            LastFailure = "Clicking Activate never produced new portals.";
            return MapDeviceResult.Failed;
        }

        // A successful activation closes the whole Atlas UI by itself (game feature) —
        // just wait for it so it can't block the portal click. No Escape needed.
        if (!await WaitFor(() => !IsAtlasOpen, 3000, ct))
            _log($"[MapDevice] Atlas did not close by itself after activation. {UiState()}");

        // Loading a fragment from the player inventory leaves the inventory panel
        // open — close it so it cannot swallow the upcoming portal click.
        if (_inventory.IsInventoryPanelOpen)
        {
            await _input.PressKey(_getInventoryKey(), ct);
            await WaitFor(() => !_inventory.IsInventoryPanelOpen, 1500, ct);
        }

        LastFailure = null;
        return MapDeviceResult.Success;
    }

    /// <summary>
    /// Generic verified UI step: skip if already satisfied, otherwise act, then
    /// frame-check the effect until it appears or times out. Every failed attempt
    /// logs the UI-state snapshot so a stuck flow is diagnosable from the log.
    /// The action is a factory so each attempt re-finds its element fresh —
    /// stale rects/addresses from before a failed attempt are never reused.
    /// </summary>
    private async SyncTask<bool> RetryStep(
        string name, Func<bool> verify, Func<SyncTask<bool>> action,
        int verifyTimeoutMs, CancellationToken ct, int attempts = 3, string? itemPath = null)
    {
        for (var i = 1; i <= attempts && !ct.IsCancellationRequested; i++)
        {
            if (verify()) return true;

            _log($"[MapDevice] {name} (attempt {i}/{attempts})...");
            var acted = await action();
            if (!acted)
                _log($"[MapDevice] {name}: action reported failure -- verifying anyway. {UiState(itemPath)}");

            if (await WaitFor(verify, verifyTimeoutMs, ct)) return true;
            _log($"[MapDevice] {name}: not confirmed within {verifyTimeoutMs}ms. {UiState(itemPath)}");
        }

        if (verify()) return true;
        _log($"[MapDevice] {name} FAILED after {attempts} attempts. {UiState(itemPath)}");
        return false;
    }

    /// <summary>
    /// One-line diagnosis of where the flow currently stands: which panels are
    /// open, whether the activate button exists, and where the fragment is.
    /// </summary>
    private string UiState(string? itemPath = null)
    {
        try
        {
            var btn = DeviceWindow?.ActivateButton;
            var s = $"[state: atlas={IsAtlasOpen} deviceWindow={IsDeviceWindowOpen} " +
                    $"activateBtn={(btn == null || btn.Address == 0 ? "null" : btn.IsVisible ? "visible" : "hidden")} " +
                    $"invPanel={_inventory.IsInventoryPanelOpen}";

            if (itemPath != null)
            {
                var shortName = itemPath[(itemPath.LastIndexOf('/') + 1)..];
                s += $" | {shortName}: mapSlot={_inventory.CountItems(_inventory.MapDeviceMapInventory, itemPath)} " +
                     $"storage={_inventory.CountItems(_inventory.MapDeviceStorage, itemPath)} " +
                     $"inventory={(_inventory.FindMainInventoryItem(itemPath) != null ? "yes" : "no")}";
            }

            return s + "]";
        }
        catch (Exception ex)
        {
            return $"[state unreadable: {ex.Message}]";
        }
    }

    private bool IsInMapSlot(string itemPath) =>
        _inventory.CountItems(_inventory.MapDeviceMapInventory, itemPath) > 0;

    private bool IsInStorage(string itemPath) =>
        _inventory.CountItems(_inventory.MapDeviceStorage, itemPath) > 0;

    private async SyncTask<MapDeviceResult> EnsureItemInMapSlot(string itemPath, CancellationToken ct)
    {
        if (IsInMapSlot(itemPath) && IsDeviceWindowOpen)
        {
            _log("[MapDevice] Map slot already loaded.");
            await StowInventorySpareToStorage(itemPath, ct);
            return MapDeviceResult.Success;
        }

        if (!IsInMapSlot(itemPath) && !IsInStorage(itemPath) &&
            _inventory.FindMainInventoryItem(itemPath) == null)
        {
            _log($"[MapDevice] No '{itemPath}' found in map slot, device storage, or inventory. Out of fragments?");
            return MapDeviceResult.NoFragments;
        }

        // Step 1: the MapDeviceWindow must be open. Right-clicking ANY fragment
        // (storage or inventory) surfaces it — including when the slot is
        // already armed: the spare's right-click just opens the window and the
        // armed map stays put. From the INVENTORY with an EMPTY slot the
        // fragment is usually armed automatically; from the STORAGE it lands
        // unarmed and needs the ctrl-click in step 2. There is deliberately no
        // click on the atlas plane itself — its pan/zoom make any coordinate
        // unreliable. With the slot armed and NO spare anywhere, the flow fails
        // here and the stash restock provides a spare for the next attempt
        // (a slot-only fragment does not count as stocked in the chores).
        if (!IsDeviceWindowOpen)
        {
            bool opened;
            if (IsInStorage(itemPath))
            {
                await EnsureStorageExpanded(ct);
                opened = await RetryStep("Open device window (right-click storage fragment)",
                    () => IsDeviceWindowOpen,
                    () => ClickStorageFragment(itemPath, MouseButtons.Right, ctrl: false, ct),
                    3000, ct, itemPath: itemPath);
            }
            else if (_inventory.FindMainInventoryItem(itemPath) != null)
            {
                if (!await EnsureInventoryPanelOpen(ct))
                {
                    LastFailure = "The inventory panel did not open.";
                    return MapDeviceResult.Failed;
                }

                opened = await RetryStep("Open device window (right-click inventory fragment)",
                    () => IsDeviceWindowOpen,
                    () => ClickInventoryFragment(itemPath, MouseButtons.Right, ctrl: false, ct),
                    3000, ct, itemPath: itemPath);
            }
            else
            {
                LastFailure = "The slot is armed but the device window is closed and no spare " +
                              "fragment is on hand to right-click it open -- waiting for a stash restock.";
                _log($"[MapDevice] {LastFailure} {UiState(itemPath)}");
                return MapDeviceResult.Failed;
            }

            if (!opened)
            {
                LastFailure = "The map device window did not open (right-clicking the fragment never registered).";
                return MapDeviceResult.Failed;
            }
            await Task.Delay(1000, ct); // window settle before reading its inventories
        }

        if (IsInMapSlot(itemPath))
        {
            // Armed (either from before, or the inventory right-click auto-armed
            // it). Park any leftover inventory spare in the storage first.
            await StowInventorySpareToStorage(itemPath, ct);
            return MapDeviceResult.Success;
        }

        // Step 2: ctrl-click a fragment into the map slot — storage first, inventory fallback.
        if (IsInStorage(itemPath))
        {
            await EnsureStorageExpanded(ct);
            if (await RetryStep("Load map slot (ctrl-click storage fragment)",
                    () => IsInMapSlot(itemPath),
                    () => ClickStorageFragment(itemPath, MouseButtons.Left, ctrl: true, ct),
                    3000, ct, itemPath: itemPath))
                return MapDeviceResult.Success;

            LastFailure = "Ctrl-clicking the storage fragment into the map slot never registered.";
            return MapDeviceResult.Failed;
        }

        if (_inventory.FindMainInventoryItem(itemPath) != null)
        {
            if (!await EnsureInventoryPanelOpen(ct))
            {
                LastFailure = "The inventory panel did not open.";
                return MapDeviceResult.Failed;
            }

            if (await RetryStep("Load map slot (ctrl-click inventory fragment)",
                    () => IsInMapSlot(itemPath),
                    () => ClickInventoryFragment(itemPath, MouseButtons.Left, ctrl: true, ct),
                    3000, ct, itemPath: itemPath))
                return MapDeviceResult.Success;

            LastFailure = "Ctrl-clicking the inventory fragment into the map slot never registered.";
            return MapDeviceResult.Failed;
        }

        LastFailure = "The fragment vanished mid-flow.";
        _log($"[MapDevice] Fragment vanished mid-flow. {UiState(itemPath)}");
        return MapDeviceResult.Failed;
    }

    /// <summary>
    /// Click a fragment element in the device storage, re-finding it fresh.
    /// Primary source: the typed storage panel's own item elements (exact rects).
    /// Fallback: the entity-bound element search over the Atlas subtree.
    /// </summary>
    private async SyncTask<bool> ClickStorageFragment(
        string itemPath, MouseButtons button, bool ctrl, CancellationToken ct)
    {
        try
        {
            var uiItem = UiStorage?.VisibleInventoryItems?
                .FirstOrDefault(it => it?.Item?.Path?.Contains(itemPath) == true);
            if (uiItem != null)
            {
                if (ctrl)
                    return await _input.CtrlClickRect(uiItem.GetClientRect(), ct);
                return await _input.ClickRect(uiItem.GetClientRect(), button, ct);
            }
        }
        catch (Exception ex)
        {
            _log($"[MapDevice] Typed storage read failed ({ex.Message}) -- falling back to element search.");
        }

        var element = FindStorageItemElement(itemPath);
        if (element == null)
        {
            _log($"[MapDevice] No clickable storage element found for '{itemPath}'.");
            DumpAtlasUiTree($"no clickable storage element for '{itemPath}'");
            return false;
        }

        return await ClickElement(element, button, ct, ctrl);
    }

    /// <summary>
    /// Click a fragment in the player inventory, re-finding it fresh.
    /// </summary>
    private async SyncTask<bool> ClickInventoryFragment(
        string itemPath, MouseButtons button, bool ctrl, CancellationToken ct)
    {
        var item = _inventory.FindMainInventoryItem(itemPath);
        if (item == null)
        {
            _log($"[MapDevice] No '{itemPath}' in the inventory (anymore).");
            return false;
        }

        if (ctrl)
            return await _input.CtrlClickRect(item.GetClientRect(), ct);
        return await _input.ClickRect(item.GetClientRect(), button, ct);
    }

    /// <summary>
    /// Find the clickable UI element for a fragment sitting in the map device storage.
    /// Primary match: element whose bound Entity address equals a storage server-inventory
    /// item (exact — verified that UI elements bind the same entity objects).
    /// Fallback: path match, excluding elements inside the MapDeviceWindow rect
    /// (that's the loaded map slot, not storage).
    /// </summary>
    private Element? FindStorageItemElement(string itemPath)
    {
        var root = AtlasRoot;
        if (root == null || !root.IsVisible) return null;

        var storageAddresses = new HashSet<long>(
            _inventory.MapDeviceStorage?.InventorySlotItems?
                .Where(s => s.Item?.Path != null && s.Item.Path.Contains(itemPath))
                .Select(s => s.Item.Address) ?? []);

        try
        {
            var matches = new List<Element>();
            root.GetAllElementsRecursive(e =>
            {
                try
                {
                    return e is { IsVisible: true } && e.Entity?.Path == itemPath;
                }
                catch
                {
                    return false;
                }
            }, matches);

            if (matches.Count == 0) return null;

            var byAddress = matches.FirstOrDefault(e => storageAddresses.Contains(e.Entity.Address));
            if (byAddress != null) return byAddress;

            var window = DeviceWindow;
            if (window?.IsVisible == true)
            {
                var windowRect = window.GetClientRectCache;
                matches.RemoveAll(e => windowRect.Contains(e.GetClientRectCache.Center));
            }

            return matches.Count > 0 ? matches[0] : null;
        }
        catch (Exception ex)
        {
            _log($"[MapDevice] Storage element search failed: {ex.Message}");
            return null;
        }
    }

    private async SyncTask<bool> ClickElement(Element element, MouseButtons button, CancellationToken ct, bool ctrl = false)
    {
        if (ctrl)
            return await _input.CtrlClickRect(element.GetClientRectCache, ct);
        return await _input.ClickRect(element.GetClientRectCache, button, ct);
    }

    private async SyncTask<bool> EnsureInventoryPanelOpen(CancellationToken ct) =>
        await RetryStep("Open inventory panel",
            () => _inventory.IsInventoryPanelOpen,
            () => _input.PressKey(_getInventoryKey(), ct),
            2000, ct);

    /// <summary>
    /// Click Activate and wait for NEW portals — portals of a previous (e.g.
    /// completed) run may still stand at the device, so "portals exist" proves
    /// nothing. Only a portal entity that did not exist before the click counts
    /// as verification; a click that didn't take is retried.
    /// </summary>
    private async SyncTask<bool> ActivateAndWaitForPortals(
        Func<string, bool> isFarmArea, CancellationToken ct)
    {
        var before = _gameState.FindFarmPortals(isFarmArea)
            .Select(p => p.Id)
            .ToHashSet();

        bool NewPortalsUp() => _gameState.FindFarmPortals(isFarmArea)
            .Any(p => !before.Contains(p.Id));

        var ok = await RetryStep("Activate map device", NewPortalsUp,
            () => ClickActivateButton(ct), 8000, ct, attempts: 2);

        if (ok)
            _log($"[MapDevice] {_gameState.FindFarmPortals(isFarmArea).Count} portals detected.");
        return ok;
    }

    private async SyncTask<bool> ClickActivateButton(CancellationToken ct)
    {
        var button = DeviceWindow?.ActivateButton;
        if (!IsDeviceWindowOpen || button == null || button.Address == 0 || !button.IsVisible)
        {
            _log("[MapDevice] Activate button not available.");
            return false;
        }

        _log("[MapDevice] Clicking Activate...");
        return await _input.ClickRect(button.GetClientRectCache, MouseButtons.Left, ct);
    }

    private static SyncTask<bool> WaitFor(Func<bool> condition, int timeoutMs, CancellationToken ct) =>
        Helpers.WaitHelpers.For(condition, timeoutMs, ct);
}
