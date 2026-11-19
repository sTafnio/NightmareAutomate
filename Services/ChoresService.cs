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
/// everything that needs the panel — inventory dump, fragment restock, omen
/// restock — in that order (dump first frees space; restocks last so the pulled
/// items are not dumped right back).
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

    /// <summary>Whether to look for an Omen of Amelioration in the restock tab when
    /// the inventory has none. Lowered when the tab turned out to hold none, so a
    /// tab without omens is not reopened on every hideout visit; raised again by
    /// <see cref="ArmOmenRestock"/> (session start, every death).</summary>
    private bool _omenCheckArmed = true;

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

    /// <summary>The omen feature is on, a restock tab is set, and the inventory
    /// holds no omen (a death consumed it, or the session started without one).</summary>
    private bool OmenMissing(string restockTabName) =>
        _settings.KeepOmenOfAmelioration &&
        !string.IsNullOrWhiteSpace(restockTabName) &&
        !_inventory.HasOmenOfAmelioration;

    private bool NeedOmen(string restockTabName) => _omenCheckArmed && OmenMissing(restockTabName);

    /// <summary>Look for an omen in the restock tab again (session start, and every
    /// death — the death is what consumes it).</summary>
    public void ArmOmenRestock() => _omenCheckArmed = true;

    /// <summary>An omen should be fetched from the restock tab now. Honours the
    /// config-error latch like the other stash-bound chores. Used by the engine
    /// to decide on the hideout trip between a death and the re-entry.</summary>
    public bool OmenRestockPending(string restockTabName)
    {
        _restockTabName = restockTabName;
        RefreshConfigLatch();
        return _configErrorSnapshot == null && NeedOmen(restockTabName);
    }

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
                               (NeedStash || NeedFragments(fragmentPaths, restockTabName) ||
                                NeedOmen(restockTabName)));
    }

    /// <summary>
    /// Run all pending chores. Stash-bound chores share one open-panel session.
    ///
    /// forceStash dumps the inventory even when it is not COMPLETELY full — for
    /// the caller that came home from a live map for no other reason than to
    /// empty it. A single item that does not fit (a 2x4 armour into three loose
    /// cells) deadlocks looting exactly like a full inventory does, and
    /// <see cref="UrgentStashPending"/> cannot see that from the hideout.
    /// </summary>
    public async SyncTask<ChoresResult> Run(
        IReadOnlyList<string> fragmentPaths, string restockTabName, CancellationToken ct,
        bool forceStash = false)
    {
        // While a config error stands, the stash-bound chores stay off (gems still
        // run) — re-attempting them would just re-fail and re-alert every visit.
        _restockTabName = restockTabName;
        RefreshConfigLatch();
        var configBlocked = _configErrorSnapshot != null;
        var needStash = !configBlocked &&
                        (NeedStash || (forceStash && _inventory.GetDumpableItems().Count > 0));
        var needFragments = !configBlocked && NeedFragments(fragmentPaths, restockTabName);
        var needOmen = !configBlocked && NeedOmen(restockTabName);
        if (!needStash && !needFragments && !needOmen)
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
                    BotLog.Step($"[Chores] Restocking a fragment from tab '{restockTabName}'...");
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

            // Not skipped when the fragment tab read empty: that verdict no longer
            // ends the session on the spot (a live run is finished first, and the
            // engine confirms "empty" with a second look).
            if (needOmen && !configError && OmenMissing(restockTabName))
            {
                switch (await RestockOmen(restockTabName, ct))
                {
                    case PullResult.ConfigError:
                        LastFailureDetail = _stashService.LastFailure;
                        configError = true;
                        break;
                    case PullResult.Failed:
                        // A failed dump before this is the root cause: keep its detail.
                        if (!failed)
                            LastFailureDetail = _stashService.LastFailure ??
                                                "Taking an Omen of Amelioration from the stash failed.";
                        failed = true;
                        break;
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

    /// <summary>
    /// The trip between a death and the re-entry: the Omen of Amelioration and
    /// NOTHING else — no dump, no fragment restock, no gem level-ups. A live run
    /// is waiting; everything else waits for the chores between runs. (A full
    /// inventory never gets here: the engine sends that to the full chores first.)
    /// </summary>
    public async SyncTask<ChoresResult> RestockOmenOnly(string restockTabName, CancellationToken ct)
    {
        _restockTabName = restockTabName;
        RefreshConfigLatch();
        if (_configErrorSnapshot != null || !NeedOmen(restockTabName))
            return ChoresResult.Done;

        if (!await _stashService.OpenStashPanel(ct))
        {
            LastFailureDetail = _stashService.LastFailure;
            return ChoresResult.Failed;
        }

        PullResult pull;
        try
        {
            pull = await RestockOmen(restockTabName, ct);
        }
        finally
        {
            await _stashService.ClosePanel(ct);
        }

        switch (pull)
        {
            case PullResult.ConfigError:
                LastFailureDetail = _stashService.LastFailure;
                _configErrorSnapshot = ConfigSnapshot;
                return ChoresResult.ConfigError;
            case PullResult.Failed:
                LastFailureDetail = _stashService.LastFailure ??
                                    "Taking an Omen of Amelioration from the stash failed.";
                return ChoresResult.Failed;
            default:
                // Pulled, or the tab holds none (RestockOmen disarmed the check).
                LastFailureDetail = null;
                return ChoresResult.Done;
        }
    }

    /// <summary>
    /// Take ONE Omen of Amelioration from the restock tab (stack size 1, so the
    /// ctrl-click moves exactly one). A tab without omens is not a failure: the
    /// session carries on without one, and the tab is only checked again after
    /// the next death. In the full chores the dump runs first, so a full
    /// inventory here means the dump failed, which is already reported.
    /// </summary>
    private async SyncTask<PullResult> RestockOmen(string restockTabName, CancellationToken ct)
    {
        if (_inventory.FreeMainInventoryCells() == 0)
        {
            _log("[Chores] No free inventory cell for an Omen of Amelioration -- skipping it this time.");
            return PullResult.Failed;
        }

        BotLog.Step($"[Chores] Taking an Omen of Amelioration from tab '{restockTabName}'...");
        var pull = await _stashService.PullItemFromTab(restockTabName, InventoryService.OmenOfAmeliorationPath, ct);
        switch (pull)
        {
            case PullResult.Pulled:
                _log("[Chores] Omen of Amelioration is back in the inventory.");
                break;
            case PullResult.Empty:
                _omenCheckArmed = false;
                _log($"[Chores] No Omen of Amelioration in tab '{restockTabName}' -- carrying on without " +
                     "one (the tab is checked again after the next death).");
                break;
        }

        return pull;
    }

    private async SyncTask<bool> LevelGemsIfPending(CancellationToken ct)
    {
        if (!NeedGems) return false;
        _gemAttemptAt = Clock.NowMs;
        await _gems.LevelReadyGems(ct);
        return true;
    }
}
