using System.Collections.Generic;
using System.Numerics;

namespace NightmareAutomate.Services;

/// <summary>
/// Positions remembered per AREA INSTANCE (keyed by area hash + a name), owned
/// by the plugin so they survive engine/strategy recreation on every bot
/// start — the reason these used to be statics scattered across three classes.
/// Two visits to the same map name get different hashes, so a new instance
/// never sees the old instance's points; trips to the hideout mid-run don't
/// wipe anything because the map instance's entries simply stay stored under
/// its own hash. Never persisted to disk.
///
/// Entry count is bounded in practice (a handful of keys per area instance,
/// a few hundred instances per session) — no pruning needed.
/// </summary>
public sealed class AreaMemory
{
    public const string SimStartKey = "sim.start";
    public const string SimStashKey = "sim.stash";
    public const string SimPortalKey = "sim.portal";
    public const string MapDeviceKey = "map-device";

    private readonly Dictionary<(uint Hash, string Key), Vector2> _points = [];

    /// <summary>After a run completes, its leftover portals still stand at the
    /// map device — they must not be re-adopted (they lead back into a finished
    /// map). Cleared when a fresh map is activated. Lives here (plugin-owned)
    /// because the engine is recreated on every bot start, and a restart right
    /// after a completed run must not re-adopt that run's leftovers either.</summary>
    public bool IgnorePortalsOfCompletedRun { get; set; }

    public Vector2? GetPoint(uint areaHash, string key) =>
        areaHash != 0 && _points.TryGetValue((areaHash, key), out var p) ? p : null;

    public void SetPoint(uint areaHash, string key, Vector2 point)
    {
        if (areaHash != 0)
            _points[(areaHash, key)] = point;
    }
}
