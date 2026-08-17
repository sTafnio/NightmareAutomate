using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using NightmareAutomate.Helpers;

namespace NightmareAutomate.Services;

/// <summary>
/// Levels up skill gems through the IngameUi.GemLvlUpPanel popups.
///
/// Verified live: the panel lists one GemLevelUpElement per pending gem, each with
/// its own LevelUpButton; a child element's text reads "Click to level up" for
/// ready gems and "Gem cannot level up" for blocked ones (level-capped, missing
/// requirements). LevelUpAllGemsButton is visible even while some gems are blocked
/// and levels only the ready ones — it is the preferred single click; per-gem
/// buttons are the fallback.
/// </summary>
public sealed class GemLevelService
{
    private const string CannotLevelText = "Gem cannot level up";

    private readonly GameStateService _gameState;
    private readonly InputService _input;
    private readonly Models.SessionStats _stats;
    private readonly Action<string> _log;

    public GemLevelService(
        GameStateService gameState, InputService input, Models.SessionStats stats, Action<string> log)
    {
        _gameState = gameState;
        _input = input;
        _stats = stats;
        _log = log;
    }

    private GemLvlUpPanel? Panel => _gameState.Controller.IngameState?.IngameUi?.GemLvlUpPanel;

    /// <summary>
    /// A gem popup is ready when its + button is up and no child says the gem
    /// cannot level (that popup variant has no clickable effect).
    /// </summary>
    private static bool IsReady(GemLevelUpElement gem) => SafeRead.Try("gems.is-ready", () =>
    {
        if (gem is not { IsVisible: true }) return false;
        if (gem.LevelUpButton is not { IsVisible: true } button || button.Address == 0) return false;
        return gem.FindChildRecursive(
            e => e?.Text?.Contains(CannotLevelText, StringComparison.OrdinalIgnoreCase) == true,
            6, 0) == null;
    });

    private IReadOnlyList<GemLevelUpElement> ReadyGems() =>
        SafeRead.Try<IReadOnlyList<GemLevelUpElement>>("gems.ready-list", () =>
        {
            var panel = Panel;
            if (panel is not { IsVisible: true }) return [];
            return panel.GemsToLvlUp?.Where(IsReady).ToList() ?? [];
        }) ?? [];

    public bool HasReadyGems() => ReadyGems().Count > 0;

    /// <summary>
    /// Level every ready gem. One LevelUpAllGemsButton click covers all of them;
    /// if the ready list does not empty out, the per-gem buttons are clicked one
    /// by one. Blocked gems ("Gem cannot level up") are never touched.
    /// Returns how many gems got leveled.
    /// </summary>
    public async SyncTask<int> LevelReadyGems(CancellationToken ct)
    {
        var ready = ReadyGems();
        if (ready.Count == 0) return 0;

        var total = ready.Count;
        _log($"[Gems] {total} gem(s) ready -- leveling...");

        var allButton = Panel?.LevelUpAllGemsButton;
        if (allButton is { IsVisible: true } && allButton.Address != 0)
        {
            await _input.ClickRect(allButton.GetClientRectCache, MouseButtons.Left, ct);
            if (await WaitHelpers.For(() => ReadyGems().Count == 0, 2500, ct))
            {
                _stats.GemsLeveled += total;
                return total;
            }
        }

        // Per-gem fallback: always re-read and click the FIRST ready popup — the
        // panel reflows after every level-up, so cached elements go stale.
        for (var i = 0; i < 12 && !ct.IsCancellationRequested; i++)
        {
            ready = ReadyGems();
            if (ready.Count == 0) break;

            var before = ready.Count;
            await _input.ClickRect(ready[0].LevelUpButton.GetClientRectCache, MouseButtons.Left, ct);
            if (!await WaitHelpers.For(() => ReadyGems().Count < before, 1500, ct))
            {
                _log("[Gems] Level-up click did not register -- leaving the rest for later.");
                break;
            }
        }

        var leveled = total - ReadyGems().Count;
        _stats.GemsLeveled += leveled;
        return leveled;
    }
}
