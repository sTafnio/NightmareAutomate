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

    /// <summary>The strategy left the map specifically to EMPTY THE INVENTORY
    /// (the in-area stash could not be opened). The engine must run the hideout
    /// chores before re-entering — an item that merely does not FIT deadlocks
    /// looting just as hard as a completely full inventory, and only this flag
    /// tells the two apart from the hideout. Consumed by that chores pass.</summary>
    public bool StashTripRequested { get; set; }

    /// <summary>Monotonic start of this run in Clock.NowMs terms (set on
    /// activation/adoption; 0 = never set). Deaths and hideout trips mid-run do
    /// NOT reset it — the abandon timer measures total time spent on the run,
    /// not time inside the map.</summary>
    public long RunStartedAt { get; set; }

    /// <summary>The per-run time limit fired: the run was walked out of and
    /// reported as complete without clearing all waves.</summary>
    public bool AbandonedByTimer { get; set; }

    public void ResetForNewRun()
    {
        RunActive = false;
        PortalsUsed = 0;
        WavesStarted = 0;
        MapEntryPoint = null;
        ReEntryRequested = false;
        StashTripRequested = false;
        RunStartedAt = Helpers.Clock.NowMs;
        AbandonedByTimer = false;
    }
}
