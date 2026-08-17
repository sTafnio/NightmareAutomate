using System.Collections.Generic;
using System.Threading;
using ExileCore.Shared;
using NightmareAutomate.Models;

namespace NightmareAutomate.Strategies;

/// <summary>
/// Defines the contract for a specific farm type.
/// Each strategy encapsulates its own boss definitions, phases, and in-map logic.
/// </summary>
public interface IFarmStrategy
{
    /// <summary>
    /// Display name for logging and the debug overlay.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Human-readable name of the current internal phase (for the overlay).
    /// </summary>
    string CurrentPhaseName { get; }

    /// <summary>
    /// Item metadata paths the engine must place into the map device for one run.
    /// Empty means the strategy does not use the map device (portals appear externally).
    /// </summary>
    IReadOnlyList<string> MapDeviceItemPaths { get; }

    /// <summary>
    /// Stash tab to restock this strategy's map-device items from (typically
    /// backed by a strategy-specific setting). Empty = no restocking; the
    /// session ends when the on-hand items run out. Keeps the generic chores
    /// pipeline farm-agnostic.
    /// </summary>
    string RestockTabName { get; }

    /// <summary>
    /// Strategy-specific progress rows for the debug overlay (e.g. wave counter).
    /// </summary>
    IEnumerable<(string Label, string Value)> GetStatusRows(RunContext run);

    /// <summary>
    /// Whether the given area (display name) belongs to this farm.
    /// Strategies with multiple possible areas (e.g. Simulacrum) match a set.
    /// </summary>
    bool IsFarmArea(string areaName);

    /// <summary>
    /// Run the entire in-map farm logic.
    /// Called repeatedly while BotState is RunningFarm.
    /// Returns true when the farm objective is complete (boss defeated, waves cleared, etc.).
    /// </summary>
    SyncTask<bool> ExecuteFarm(RunContext run, CancellationToken ct);

    /// <summary>
    /// Reset internal state for a new run (new map instance).
    /// </summary>
    void Reset();
}
