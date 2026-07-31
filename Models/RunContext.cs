namespace NightmareAutomate.Models;

/// <summary>
/// Mutable state for the current farming run, shared between the engine and the strategy.
/// Survives death/re-entry; recreated when a new map device activation starts a fresh run.
/// </summary>
public sealed class RunContext
{
    public const int MaxPortals = 6;

    /// <summary>A map device activation is live — portals exist or the map is in progress.</summary>
    public bool RunActive { get; set; }

    /// <summary>How many of the 6 portals have been consumed (entries + deaths).</summary>
    public int PortalsUsed { get; set; }

    public int PortalsRemaining => MaxPortals - PortalsUsed;

    /// <summary>Waves started in the current Simulacrum (strategy-maintained).</summary>
    public int WavesStarted { get; set; }

    /// <summary>Completed full runs since the bot was started.</summary>
    public int RunsCompleted { get; set; }

    /// <summary>Where the player stood right after entering the map (grid coords).
    /// The return portals stand here; used to walk back when they are not yet
    /// streamed into memory.</summary>
    public System.Numerics.Vector2? MapEntryPoint { get; set; }

    /// <summary>Strategy asks the engine to leave the map and come back through a
    /// remaining portal (fresh instance UI) — e.g. when the wave initiator refuses
    /// to react to clicks. The run stays active; portals get consumed as usual.</summary>
    public bool ReEntryRequested { get; set; }

    public void ResetForNewRun()
    {
        RunActive = false;
        PortalsUsed = 0;
        WavesStarted = 0;
        MapEntryPoint = null;
        ReEntryRequested = false;
    }
}
