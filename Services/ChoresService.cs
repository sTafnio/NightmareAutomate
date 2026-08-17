using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ExileCore.Shared;
using NightmareAutomate.Helpers;

namespace NightmareAutomate.Services;

public enum ChoresResult
{
    /// <summary>Everything pending got handled (or nothing was pending).</summary>
    Done,

    /// <summary>Fragment restock found the input tab empty/missing — the farming
    /// session is out of fragments.</summary>
    OutOfFragments,

    /// <summary>The stash session could not run (panel would not open, dump failed).
    /// Transient — retrying can help.</summary>
    Failed,

    /// <summary>The tab settings are wrong (missing dump tabs / restock tab).
    /// Retrying cannot help — only a settings change can. Chores stay paused
    /// (HasPending returns false) until the relevant settings actually change.</summary>
    ConfigError
}

/// <summary>
/// Hideout chores between runs: gem level-ups, then ONE stash session covering
/// everything that needs the panel — inventory dump, fragment restock — in that
/// order (dump first frees space; restock last so the pulled fragment is not
/// dumped right back).
///
/// The restock tab name comes from the active strategy (via the engine), not
/// from a strategy-specific setting read here — this service stays farm-agnostic.
/// </summary>
public sealed class ChoresService
{
    private readonly StashService _stashService;
    private readonly GemLevelService _gems;
    private readonly InventoryService _inventory;
    private readonly NightmareAutomateSettings _settings;
    private readonly Action<string> _log;

    private long _gemAttemptAt = Clock.Never;

    /// <summary>The strategy's restock tab as last passed in — kept only so the
    /// parameterless latch checks (ConfigErrorStanding) can compare snapshots.</summary>
    private string _restockTabName = "";

    /// <summary>The tab settings as they were when a ConfigError was reported —
    /// while they are unchanged, retrying is pointless and chores stay paused.
    /// Settings are live-editable, so a user fixing the tab name resumes chores
    /// automatically.</summary>
    private string? _configErrorSnapshot;

    private string ConfigSnapshot => $"{_settings.DumpTabNames}|{_restockTabName}";

    /// <summary>Failing-step description of the last failed/misconfigured chore —
    /// forwarded into engine webhooks.</summary>
    public string? LastFailureDetail { get; private set; }

    /// <summary>The inventory is completely full — the state where skipping or
    /// backing off stash chores means farming blind (loot can no longer be picked).</summary>
    public bool UrgentStashPending => _stashService.ShouldStash();

    /// <summary>A reported tab-settings problem still stands (the settings have not
    /// changed since). The engine uses this to stop the session when it coincides
    /// with a full inventory instead of farming blind.</summary>
    public bool ConfigErrorStanding
    {
        get
        {
            RefreshConfigLatch();
            return _configErrorSnapshot != null;
        }
    }

    /// <summary>Settings are live-editable — a latch whose snapshot no longer
    /// matches them is stale and must lift so chores resume automatically.</summary>
    private void RefreshConfigLatch()
    {
        if (_configErrorSnapshot != null && _configErrorSnapshot != ConfigSnapshot)
            _configErrorSnapshot = null;
    }

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
        Clock.SecondsSince(_gemAttemptAt) > 30 &&
        _gems.HasReadyGems();

    /// <summary>A fragment path with no SPARE in device storage or player
    /// inventory. The map slot deliberately does NOT count: a fragment armed in
    /// the slot cannot reopen a closed device window (that needs right-clicking
    /// a spare), so "slot loaded, nothing else" must still trigger a restock —
    /// the spare gets parked in the device storage and serves the next run.
    /// (The device storage reads 0 while never opened this session — a spurious
    /// restock then is harmless: the inventory count stops it from repeating.)</summary>
    private bool FragmentMissing(string path) =>
        _inventory.CountItems(_inventory.MapDeviceStorage, path) == 0 &&
        _inventory.CountItems(_inventory.MainInventory, path) == 0;

    private bool NeedFragments(IReadOnlyList<string> fragmentPaths, string restockTabName) =>
        CanRestock(fragmentPaths, restockTabName) && fragmentPaths.Any(FragmentMissing);

    /// <summary>Whether a fragment restock from the stash tab is configured at all —
    /// the engine uses this to tell "out of fragments" apart from "restock never ran".</summary>
    public bool CanRestock(IReadOnlyList<string> fragmentPaths, string restockTabName) =>
        fragmentPaths.Count > 0 && !string.IsNullOrWhiteSpace(restockTabName);

    private long _pendingCheckedAt = Clock.Never;
    private bool _pendingLast;

    /// <summary>
    /// Anything worth entering the chores state for? Cached for 1s — the engine
    /// asks every frame while in the hideout, and the underlying checks scan
    /// server inventories and UI panels.
    /// </summary>
    public bool HasPending(IReadOnlyList<string> fragmentPaths, string restockTabName)
    {
        if (Clock.SecondsSince(_pendingCheckedAt) < 1)
            return _pendingLast;

        // A standing config error pauses the STASH-bound chores — retrying a
        // wrong tab name is pointless until the settings change. Gems don't need
        // the stash and keep running.
        _restockTabName = restockTabName;
        RefreshConfigLatch();

        _pendingCheckedAt = Clock.NowMs;
        return _pendingLast = NeedGems ||
                              (_configErrorSnapshot == null &&
                               (NeedStash || NeedFragments(fragmentPaths, restockTabName)));
    }

    /// <summary>
    /// Run all pending chores. Stash-bound chores share one open-panel session.
    /// </summary>
    public async SyncTask<ChoresResult> Run(
        IReadOnlyList<string> fragmentPaths, string restockTabName, CancellationToken ct)
    {
        // While a config error stands, the stash-bound chores stay off (gems still
        // run) — re-attempting them would just re-fail and re-alert every visit.
        _restockTabName = restockTabName;
        RefreshConfigLatch();
        var configBlocked = _configErrorSnapshot != null;
        var needStash = !configBlocked && NeedStash;
        var needFragments = !configBlocked && NeedFragments(fragmentPaths, restockTabName);
        if (!needStash && !needFragments)
        {
            await LevelGemsIfPending(ct);
            return ChoresResult.Done;
        }

        if (!await _stashService.OpenStashPanel(ct))
        {
            LastFailureDetail = _stashService.LastFailure;
            return ChoresResult.Failed;
        }

        var failed = false;
        var configError = false;
        var outOfFragments = false;
        try
        {
            if (needStash)
            {
                var dump = await _stashService.Execute(ct, keepOpen: true);
                if (dump != StashResult.Ok)
                {
                    LastFailureDetail = _stashService.LastFailure;
                    if (dump == StashResult.ConfigError) configError = true;
                    else failed = true;
                }
            }

            if (needFragments && !configError)
            {
                foreach (var path in fragmentPaths)
                {
                    if (!FragmentMissing(path)) continue;
                    _log($"[Chores] Restocking a fragment from tab '{restockTabName}'...");
                    var pull = await _stashService.PullItemFromTab(restockTabName, path, ct);
                    if (pull == PullResult.Empty)
                    {
                        outOfFragments = true;
                        break;
                    }

                    if (pull is PullResult.Failed or PullResult.ConfigError)
                    {
                        LastFailureDetail = _stashService.LastFailure;
                        if (pull == PullResult.ConfigError) configError = true;
                        else failed = true;
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
        if (configError)
        {
            _configErrorSnapshot = ConfigSnapshot;
            return ChoresResult.ConfigError;
        }

        if (failed) return ChoresResult.Failed;
        LastFailureDetail = null;
        return ChoresResult.Done;
    }

    private async SyncTask<bool> LevelGemsIfPending(CancellationToken ct)
    {
        if (!NeedGems) return false;
        _gemAttemptAt = Clock.NowMs;
        await _gems.LevelReadyGems(ct);
        return true;
    }
}
