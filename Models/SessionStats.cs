namespace NightmareAutomate.Models;

/// <summary>
/// Session-lifetime counters shared between services and the engine. Survives
/// engine restarts (created once per plugin init); incremented on the game
/// thread only. Per-run values are computed by the engine as deltas between
/// run-start snapshots and current values.
/// </summary>
public sealed class SessionStats
{
    public int Deaths { get; set; }
    public int GemsLeveled { get; set; }
}
