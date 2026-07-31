using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ExileCore.Shared;

namespace NightmareAutomate.Services;

public enum ChoresResult
{
    /// <summary>Everything pending got handled (or nothing was pending).</summary>
    Done,

    /// <summary>Fragment restock found the input tab empty/missing — the farming
    /// session is out of fragments.</summary>
    OutOfFragments,

    /// <summary>The stash session could not run (panel would not open, dump failed).</summary>
    Failed
}

/// <summary>
/// Hideout chores between runs: gem level-ups, then ONE stash session covering
/// everything that needs the panel — inventory dump, fragment restock — in that
/// order (dump first frees space; restock last so the pulled fragment is not
/// dumped right back).
/// </summary>
public sealed class ChoresService
{
    private readonly StashService _stashService;
    private readonly GemLevelService _gems;
    private readonly InventoryService _inventory;
    private readonly NightmareAutomateSettings _settings;
    private readonly Action<string> _log;

    private DateTime _gemAttemptAt = DateTime.MinValue;

    public ChoresService(
        StashService stashService,
        GemLevelService gems,
        InventoryService inventory,
        NightmareAutomateSettings settings,
        Action<string> log)
    {
        _stashService = stashService;
        _gems = gems;
        _inventory = inventory;
        _settings = settings;
        _log = log;
    }

    private bool NeedStash => _stashService.ShouldStash();

    /// <summary>Throttled: a gem click that keeps failing must not loop the
    /// hideout states — one attempt per 30s window.</summary>
    private bool NeedGems =>
        _settings.LevelGems &&
        (DateTime.Now - _gemAttemptAt).TotalSeconds > 30 &&
        _gems.HasReadyGems();

    /// <summary>A fragment path with no stock anywhere we control: map slot, device
    /// storage, player inventory. (The device storage reads 0 while never opened
    /// this session — a spurious restock then is harmless: the inventory count
    /// stops it from repeating.)</summary>
    private bool FragmentMissing(string path) =>
        _inventory.CountItems(_inventory.MapDeviceMapInventory, path) == 0 &&
        _inventory.CountItems(_inventory.MapDeviceStorage, path) == 0 &&
        _inventory.CountItems(_inventory.MainInventory, path) == 0;

    private bool NeedFragments(IReadOnlyList<string> fragmentPaths) =>
        CanRestock(fragmentPaths) && fragmentPaths.Any(FragmentMissing);

    /// <summary>Whether a fragment restock from the stash tab is configured at all —
    /// the engine uses this to tell "out of fragments" apart from "restock never ran".</summary>
    public bool CanRestock(IReadOnlyList<string> fragmentPaths) =>
        fragmentPaths.Count > 0 &&
        !string.IsNullOrWhiteSpace(_settings.SimulacrumTabName);

    private DateTime _pendingCheckedAt = DateTime.MinValue;
    private bool _pendingLast;

    /// <summary>
    /// Anything worth entering the chores state for? Cached for 1s — the engine
    /// asks every frame while in the hideout, and the underlying checks scan
    /// server inventories and UI panels.
    /// </summary>
    public bool HasPending(IReadOnlyList<string> fragmentPaths)
    {
        if ((DateTime.Now - _pendingCheckedAt).TotalSeconds < 1)
            return _pendingLast;

        _pendingCheckedAt = DateTime.Now;
        return _pendingLast = NeedStash || NeedGems || NeedFragments(fragmentPaths);
    }

    /// <summary>
    /// Run all pending chores. Stash-bound chores share one open-panel session.
    /// </summary>
    public async SyncTask<ChoresResult> Run(IReadOnlyList<string> fragmentPaths, CancellationToken ct)
    {
        var needStash = NeedStash;
        var needFragments = NeedFragments(fragmentPaths);
        if (!needStash && !needFragments)
        {
            await LevelGemsIfPending(ct);
            return ChoresResult.Done;
        }

        if (!await _stashService.OpenStashPanel(ct))
            return ChoresResult.Failed;

        var failed = false;
        var outOfFragments = false;
        try
        {
            if (needStash && !await _stashService.Execute(ct, keepOpen: true))
                failed = true;

            if (needFragments)
            {
                foreach (var path in fragmentPaths)
                {
                    if (!FragmentMissing(path)) continue;
                    _log($"[Chores] Restocking a fragment from tab '{_settings.SimulacrumTabName}'...");
                    var pull = await _stashService.PullItemFromTab(_settings.SimulacrumTabName, path, ct);
                    if (pull == PullResult.Empty)
                    {
                        outOfFragments = true;
                        break;
                    }

                    if (pull == PullResult.Failed)
                    {
                        failed = true;
                        break;
                    }
                }
            }
        }
        finally
        {
            await _stashService.ClosePanel(ct);
        }

        // Gems go LAST — only after every stash step is fully done does the mouse
        // get committed to the level-up popups.
        await LevelGemsIfPending(ct);

        if (outOfFragments) return ChoresResult.OutOfFragments;
        return failed ? ChoresResult.Failed : ChoresResult.Done;
    }

    private async SyncTask<bool> LevelGemsIfPending(CancellationToken ct)
    {
        if (!NeedGems) return false;
        _gemAttemptAt = DateTime.Now;
        await _gems.LevelReadyGems(ct);
        return true;
    }
}
