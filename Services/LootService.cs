using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared;
using NightmareAutomate.Helpers;

namespace NightmareAutomate.Services;

public enum LootResult
{
    /// <summary>Nothing left to pick up.</summary>
    AllPicked,

    /// <summary>An item does not fit — caller should stash and retry.</summary>
    InventoryFull,

    /// <summary>The abort condition fired (e.g. monsters nearby).</summary>
    Aborted,

    /// <summary>Time ran out or items kept failing; loot may remain.</summary>
    Incomplete
}

/// <summary>
/// Self-contained, knowledge-driven item pickup.
///
/// IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels is the single source of
/// "wanted" — the in-game loot filter decides what gets a label. Knowledge is NOT
/// limited to what is currently on screen or in click range:
/// - a memory of item spots seen during waves is kept and
///   visited after the wave, even when the entities have left the network bubble;
/// - labels rendered off screen just mean the item is off camera — the executor walks
///   to the entity's position, which brings the label into the viewport, then clicks.
/// Unusable labels in camera range are handled cheapest-first: label-key double-press
/// re-renders before any walking.
/// </summary>
public sealed class LootService
{
    private readonly GameStateService _gameState;
    private readonly InventoryService _inventory;
    private readonly NavigationService _navigation;
    private readonly InputService _input;
    private readonly Action<string> _log;
    private readonly Func<Keys> _getToggleLabelsKey;

    /// <summary>Ground-item spots seen at some point but possibly out of the network
    /// bubble now (entity id → grid position). Cleared when a new wave starts —
    /// the game destroys ground items then.</summary>
    private readonly Dictionary<uint, Vector2> _rememberedSpots = [];
    private long _observedAt = Clock.Never;

    public LootService(
        GameStateService gameState,
        InventoryService inventory,
        NavigationService navigation,
        InputService input,
        Action<string> log,
        Func<Keys> getToggleLabelsKey)
    {
        _gameState = gameState;
        _inventory = inventory;
        _navigation = navigation;
        _input = input;
        _log = log;
        _getToggleLabelsKey = getToggleLabelsKey;
    }

    /// <summary>
    /// All filter-approved ground item labels in the network bubble — no distance or
    /// on-screen restriction.
    /// </summary>
    public IReadOnlyList<ItemsOnGroundLabelElement.VisibleGroundItemDescription> GetWantedLabels(
        ISet<uint>? excludeIds = null)
    {
        var labels = _gameState.Controller.IngameState?.IngameUi?
            .ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
        if (labels == null) return [];

        return labels
            .Where(l => l.Entity is { IsValid: true } e &&
                        (excludeIds == null || !excludeIds.Contains(e.Id)) &&
                        e.HasComponent<WorldItem>())
            .OrderBy(l => l.Entity.DistancePlayer)
            .ToList();
    }

    /// <summary>
    /// The CURRENT label description for one entity from the live list, or null
    /// when it is gone. Label descriptions only hold addresses into game memory,
    /// and the game reuses/reflows label elements — a description snapshotted a
    /// moment ago can already render a different item, so anything that hovers
    /// or clicks must re-resolve through this first.
    /// </summary>
    private ItemsOnGroundLabelElement.VisibleGroundItemDescription? FindLabel(uint entityId)
    {
        var labels = _gameState.Controller.IngameState?.IngameUi?
            .ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
        if (labels == null) return null;

        foreach (var l in labels)
        {
            if (l.Entity is { IsValid: true } e && e.Id == entityId)
                return l;
        }

        return null;
    }

    /// <summary>
    /// Cheap single-item check: is the label for this entity gone from the ground?
    /// Used in per-frame waits where the full sorted <see cref="GetWantedLabels"/>
    /// scan would be wasteful.
    /// </summary>
    private bool LabelGone(uint entityId) => FindLabel(entityId) == null;

    /// <summary>
    /// Record the spots of all currently known ground items. Call opportunistically
    /// (fighting, patrolling) — throttled internally. Items whose entities later
    /// leave the network bubble stay remembered until visited or forgotten.
    /// </summary>
    public void ObserveGroundLoot()
    {
        if (Clock.SecondsSince(_observedAt) < 1) return;
        _observedAt = Clock.NowMs;

        foreach (var l in GetWantedLabels())
            _rememberedSpots[l.Entity.Id] = l.Entity.GridPosNum;
    }

    /// <summary>
    /// Drop all remembered spots — ground items are destroyed when the next wave starts.
    /// </summary>
    public void ForgetAll() => _rememberedSpots.Clear();

    /// <summary>
    /// Is there any loot we know of — in the bubble or remembered from earlier?
    /// Not limited by screen or click range: used by the "never start a wave while
    /// loot exists" guard.
    /// </summary>
    public bool HasKnownLoot() => _rememberedSpots.Count > 0 || GetWantedLabels().Count > 0;

    /// <summary>
    /// Pick up everything known until done, aborted, or out of space. No wall-clock
    /// timeout: every item ends in picked / deferred / given-up, so the loop
    /// terminates on its own.
    /// </summary>
    public async SyncTask<LootResult> PickupWindow(Func<bool>? abortWhen, CancellationToken ct)
    {
        const int maxRetryRounds = 2;

        var deferred = new HashSet<uint>();
        var failedClicks = new Dictionary<uint, int>();
        var losWalks = new Dictionary<uint, int>();
        var cameraWalks = new Dictionary<uint, int>();
        var fixAttempts = new Dictionary<uint, int>();
        var retryRound = 0;
        var sawUnfittable = false;
        var lastChanceDone = false;
        var emptySince = (long?)null;

        while (!ct.IsCancellationRequested)
        {
            if (abortWhen?.Invoke() == true)
            {
                await _navigation.ReleaseMoveKey();
                return LootResult.Aborted;
            }

            ObserveGroundLoot();

            var labels = GetWantedLabels(deferred);
            if (labels.Count == 0)
            {
                // Nothing in the bubble — visit remembered spots from during the wave.
                var spot = NearestRememberedSpot(deferred);
                if (spot != null)
                {
                    var (id, pos) = spot.Value;
                    _log($"[Loot] Walking to a remembered drop at {pos}...");
                    var reached = await _navigation.MoveToTarget(pos, ct, 12);
                    if (!reached || LabelGone(id))
                        _rememberedSpots.Remove(id); // gone (picked/never real) or unreachable
                    continue;
                }

                // Everything that fits is handled — surface the space problem so the
                // caller can stash and re-loot.
                if (sawUnfittable)
                {
                    await _navigation.ReleaseMoveKey();
                    return LootResult.InventoryFull;
                }

                // Deferred items get retry rounds. Round 1 is a PLAIN retry, no
                // re-render: most deferrals are click/highlight misses whose
                // labels are fine — and keeping the per-item budgets means
                // label-broken items (which already got their own toggles) fall
                // straight through to round 2. Round 2 re-renders everything and
                // resets the budgets for one full second chance.
                if (deferred.Count > 0 && retryRound < maxRetryRounds)
                {
                    retryRound++;
                    if (retryRound >= 2)
                    {
                        _log($"[Loot] Re-rendering labels to retry {deferred.Count} skipped items (round {retryRound}).");
                        await ToggleLabels(ct);
                        losWalks.Clear();
                        cameraWalks.Clear();
                        fixAttempts.Clear();
                    }
                    else
                    {
                        _log($"[Loot] Retrying {deferred.Count} skipped items (round {retryRound}).");
                    }

                    deferred.Clear();
                    failedClicks.Clear();
                    emptySince = null;
                    continue;
                }

                // Loot trickles in — require a short stable-empty period.
                emptySince ??= Clock.NowMs;
                if (Clock.MsSince(emptySince.Value) > 700)
                {
                    if (deferred.Count > 0)
                    {
                        // Final escalation before giving up: walk ON TOP of each
                        // resisting item. Highlight-refusing items (something
                        // overlapping the label from this angle) never got a walk
                        // in the normal flow — standing on the item changes the
                        // whole projection.
                        if (!lastChanceDone)
                        {
                            lastChanceDone = true;
                            _log($"[Loot] {deferred.Count} items resisted -- walking onto each for a final try.");
                            await LastChancePass(deferred, abortWhen, ct);
                            emptySince = null;
                            continue;
                        }

                        _log($"[Loot] Giving up on {deferred.Count} items with unusable labels.");
                        return LootResult.Incomplete;
                    }
                    return LootResult.AllPicked;
                }

                await TaskUtils.NextFrame();
                continue;
            }

            emptySince = null;
            var label = labels[0];
            var groundEntity = label.Entity;

            // Fit pre-check (stack merge aware). Skip and keep collecting what fits;
            // the stash trip happens once at the end.
            var itemEntity = groundEntity.GetComponent<WorldItem>()?.ItemEntity;
            if (itemEntity != null && !_inventory.CanFitItem(itemEntity))
            {
                if (!sawUnfittable)
                    _log($"[Loot] '{itemEntity.Path}' does not fit -- finishing the fittable items first.");
                sawUnfittable = true;
                deferred.Add(groundEntity.Id);
                continue;
            }

            // In-game pickup needs line of sight to the ITEM, not just a clickable
            // label: just behind a wall edge an item can check every box, yet the
            // character can't loot it and the game's own click-pathing may fail.
            // No sight → walk the real route toward the item, stopping the moment
            // sight opens, then run the normal label routine.
            if (!_gameState.HasLineOfSight(_gameState.PlayerGridPos, groundEntity.GridPosNum))
            {
                var walks = losWalks.GetValueOrDefault(groundEntity.Id);
                if (walks < 3)
                {
                    losWalks[groundEntity.Id] = walks + 1;
                    await _navigation.MoveToTarget(groundEntity.GridPosNum, ct, 6,
                        pathArrival: true,
                        stopWhen: () => _gameState.HasLineOfSight(
                            _gameState.PlayerGridPos, groundEntity.GridPosNum));
                }
                else
                {
                    deferred.Add(groundEntity.Id);
                }

                continue;
            }

            // The only click criterion: the label is truly on screen with margins.
            if (!IsLabelClickable(label))
            {
                // Far off camera: no amount of re-rendering brings the label into
                // the viewport — walk into camera range first (the label usually
                // enters the viewport well before the item).
                if (groundEntity.DistancePlayer > 55 &&
                    cameraWalks.GetValueOrDefault(groundEntity.Id) < 2)
                {
                    cameraWalks[groundEntity.Id] = cameraWalks.GetValueOrDefault(groundEntity.Id) + 1;
                    await _navigation.MoveToTarget(groundEntity.GridPosNum, ct, 40);
                    continue;
                }

                // In camera range but the label is unusable. Most of these are
                // render problems, not distance problems — try the cheap label
                // re-render twice before walking onto the item (path-based
                // arrival: it can be euclidean-close but behind a wall), then
                // defer it to the retry rounds.
                var tries = fixAttempts.GetValueOrDefault(groundEntity.Id);
                fixAttempts[groundEntity.Id] = tries + 1;

                if (tries < 2)
                    await ToggleLabels(ct);
                else if (tries < 4)
                    await _navigation.MoveToTarget(groundEntity.GridPosNum, ct, 12, pathArrival: true);
                else
                    deferred.Add(groundEntity.Id);

                continue;
            }

            // Click the label — the game auto-walks the character to the item, so
            // don't re-click while the player is still on the way; wait until the
            // item is gone or the character stopped without picking it.
            await _navigation.ReleaseMoveKey();
            // The label projection keeps shifting while the character glides to a
            // stop — hovering mid-glide lands the cursor where the label WAS.
            // Waiting for standstill (and reading ClientRect only after it) makes
            // the highlight check below actually test the label, not the glide.
            await _navigation.WaitForStandstill(ct);

            // Re-resolve the label for THIS entity from the live list: the game
            // reflows/reuses label elements (heaviest during the wave-reward spawn
            // burst), so the snapshotted description can already render a
            // DIFFERENT item by now — hovering it would highlight-check the right
            // entity under the wrong label and always miss.
            var freshLabel = FindLabel(groundEntity.Id);
            if (freshLabel == null)
                continue; // label vanished while stopping — re-evaluate next pass
            label = freshLabel;
            if (!IsLabelClickable(label))
                continue; // rect moved meanwhile — the unusable-label branch retries

            await _input.MoveMouse(_input.RandomScreenPoint(label.ClientRect), ct);

            // The item MUST highlight (Targetable.isTargeted) under the cursor
            // before the click — no highlight means something draws over the label
            // (e.g. the next wave's modifier window). Never click blind: defer the
            // item, collect everything else, and the re-render round retries it.
            var targeted = await Helpers.WaitHelpers.For(
                () => groundEntity.GetComponent<Targetable>()?.isTargeted == true, 600, ct);

            if (!targeted)
            {
                _log($"[Loot] Item never highlighted under cursor -- deferring it ({groundEntity.Path}).");
                deferred.Add(groundEntity.Id);
                continue;
            }

            await _input.Click(MouseButtons.Left, ct);

            // Success signal: the label is gone from the ground (item picked).
            // Early miss detection: a click that missed leaves the character
            // standing still with the label still up — bail after a short
            // standstill instead of burning the full cap, the failed-click
            // handling below re-clicks immediately.
            var clickedAt = Clock.NowMs;
            await Helpers.WaitHelpers.For(() =>
                LabelGone(groundEntity.Id) ||
                (!_gameState.IsPlayerMoving &&
                 Clock.MsSince(clickedAt) > 800), 5000, ct);

            var picked = LabelGone(groundEntity.Id);
            if (picked)
            {
                _rememberedSpots.Remove(groundEntity.Id);
            }
            else
            {
                // One failed click can be transient; repeat offenders get deferred to
                // the re-render round.
                var fails = failedClicks.GetValueOrDefault(groundEntity.Id) + 1;
                failedClicks[groundEntity.Id] = fails;
                if (fails >= 2)
                    deferred.Add(groundEntity.Id);
            }
        }

        await _navigation.ReleaseMoveKey();
        return HasKnownLoot() ? LootResult.Incomplete : LootResult.AllPicked;
    }

    /// <summary>
    /// Walk on top of each failed item (path-based arrival — it may sit behind a
    /// wall edge) and try one more hover+click from point-blank. Standing on the
    /// item gives a completely different projection than the failed from-range
    /// attempts. The highlight is still awaited but the click goes out regardless —
    /// this is the last try before the item is abandoned. Picked items are removed
    /// from the failed set.
    /// </summary>
    private async SyncTask<int> LastChancePass(HashSet<uint> failedIds, Func<bool>? abortWhen, CancellationToken ct)
    {
        var picked = 0;
        foreach (var id in failedIds.ToList())
        {
            if (ct.IsCancellationRequested || abortWhen?.Invoke() == true) break;

            var label = FindLabel(id);
            if (label == null)
            {
                // Already gone (picked earlier / despawned) — nothing to rescue.
                failedIds.Remove(id);
                _rememberedSpots.Remove(id);
                picked++;
                continue;
            }

            var groundEntity = label.Entity;
            _log($"[Loot] Last chance: moving onto the item ({groundEntity.Path}).");
            await _navigation.MoveToTarget(groundEntity.GridPosNum, ct, 8, pathArrival: true);
            await _navigation.ReleaseMoveKey();
            await _navigation.WaitForStandstill(ct);

            label = FindLabel(id);
            if (label == null)
            {
                failedIds.Remove(id);
                _rememberedSpots.Remove(id);
                picked++;
                continue;
            }

            if (!IsLabelClickable(label))
                continue;

            await _input.MoveMouse(_input.RandomScreenPoint(label.ClientRect), ct);
            await Helpers.WaitHelpers.For(
                () => groundEntity.GetComponent<Targetable>()?.isTargeted == true, 600, ct);
            await _input.Click(MouseButtons.Left, ct);
            await Helpers.WaitHelpers.For(() => LabelGone(id), 3000, ct);

            if (LabelGone(id))
            {
                failedIds.Remove(id);
                _rememberedSpots.Remove(id);
                picked++;
            }
        }

        return picked;
    }

    private (uint Id, Vector2 Pos)? NearestRememberedSpot(ISet<uint> excludeIds)
    {
        if (_rememberedSpots.Count == 0) return null;

        var playerPos = _gameState.PlayerGridPos;
        (uint Id, Vector2 Pos)? best = null;
        var bestDist = float.MaxValue;

        foreach (var (id, pos) in _rememberedSpots)
        {
            if (excludeIds.Contains(id)) continue;
            var d = Vector2.DistanceSquared(playerPos, pos);
            if (d < bestDist)
            {
                bestDist = d;
                best = (id, pos);
            }
        }

        return best;
    }

    /// <summary>
    /// A label is clickable when its rect is sane and its center lies inside the
    /// window with a safety margin.
    /// </summary>
    private bool IsLabelClickable(ItemsOnGroundLabelElement.VisibleGroundItemDescription label)
    {
        var rect = label.ClientRect;
        if (rect.Width < 2 || rect.Height < 2) return false;

        var window = _gameState.WindowRect;
        return Helpers.CoordinateHelpers.IsInsideWindow(
            new Vector2(rect.Center.X, rect.Center.Y), window.Width, window.Height,
            Helpers.CoordinateHelpers.ClickMarginX, Helpers.CoordinateHelpers.ClickMarginY);
    }

    /// <summary>
    /// Re-render all ground labels: a quick hide/show double press of the toggle
    /// key — the game rebuilds the labels on the show, no dwell time needed.
    /// </summary>
    private async SyncTask<bool> ToggleLabels(CancellationToken ct)
    {
        var key = _getToggleLabelsKey();
        await _input.PressKey(key, ct);
        await Task.Delay(100, ct);
        await _input.PressKey(key, ct);
        await Task.Delay(300, ct);
        return true;
    }
}
