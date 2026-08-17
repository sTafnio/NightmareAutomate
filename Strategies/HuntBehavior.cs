using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using NightmareAutomate.Helpers;
using NightmareAutomate.Services;

namespace NightmareAutomate.Strategies;

/// <summary>
/// Finding the REST of a wave when nothing targetable is near us: chasing the
/// invisible pack-spawner daemons, walking to last-known positions of monsters
/// that streamed out of the network bubble, and the hunt-frustration watchdog
/// that relocates the character when those hints keep leading into the same
/// corner. Split out of the strategy so the phase machine stays a phase machine.
/// </summary>
public sealed class HuntBehavior
{
    /// <summary>Invisible spawn-point daemons scattered across the area during a wave.
    /// Walking near one activates its pack — chasing these replaces blind patrolling.</summary>
    public const string SpawnerDaemonPathFragment = "LeagueAffliction/AfflictionatorPackSpawnerDaemon";

    /// <summary>Hunting this long without seeing a monster or the server counter
    /// dropping triggers the shake-off relocation.</summary>
    private const int HuntFrustrationSeconds = 40;

    private static readonly Random HuntRandom = new();

    private readonly GameStateService _gameState;
    private readonly NavigationService _navigation;
    private readonly ExplorationService _exploration;
    private readonly Func<IReadOnlyList<Entity>> _getNearbyMonsters;
    private readonly string[] _ignoredPathFragments;
    private readonly Action<string> _log;

    private readonly Dictionary<uint, long> _daemonSkipUntil = [];
    private readonly Dictionary<uint, int> _daemonFailCounts = [];

    private long _huntingSince = Clock.Never;
    private int _huntLastRemaining;
    private int _huntShakeoffs;

    /// <param name="ignoredPathFragments">Monster paths never worth hunting
    /// (volatiles, aura pylons, the initiator itself).</param>
    public HuntBehavior(
        GameStateService gameState,
        NavigationService navigation,
        ExplorationService exploration,
        Func<IReadOnlyList<Entity>> getNearbyMonsters,
        string[] ignoredPathFragments,
        Action<string> log)
    {
        _gameState = gameState;
        _navigation = navigation;
        _exploration = exploration;
        _getNearbyMonsters = getNearbyMonsters;
        _ignoredPathFragments = ignoredPathFragments;
        _log = log;
    }

    /// <summary>Contact with monsters = the hunt worked; disarm the frustration guard.</summary>
    public void NoteContact()
    {
        _huntingSince = Clock.Never;
        _huntShakeoffs = 0;
    }

    /// <summary>
    /// Hunt watchdog. Hunting = wave alive, nothing targetable near us. The clock
    /// (re)arms on progress — first hunt tick, monster contact (NoteContact), or
    /// the server counter dropping. Past the threshold: first strike walks to a
    /// RANDOM far tour point (ignoring the daemon/last-known hints that keep
    /// leading into the same corner), second strike walks a full exploration lap.
    /// Returns true when a shake-off walk ran this tick.
    /// </summary>
    public async SyncTask<bool> ShakeOffIfStuck(CancellationToken ct)
    {
        var remaining = _gameState.MonstersRemaining;

        if (_huntingSince == Clock.Never)
        {
            // Hunt just started — arm the clock, no shake-off yet.
            _huntingSince = Clock.NowMs;
            _huntLastRemaining = remaining;
            return false;
        }

        if (remaining < _huntLastRemaining)
        {
            // Something died (ReAgent off-screen kill, volatile, ...) — progress.
            _huntingSince = Clock.NowMs;
            _huntLastRemaining = remaining;
            _huntShakeoffs = 0;
            return false;
        }

        // More spawns registering is not hunt progress, but track the high-water
        // mark so a later kill still reads as a decrease.
        if (remaining > _huntLastRemaining)
            _huntLastRemaining = remaining;

        if (Clock.SecondsSince(_huntingSince) < HuntFrustrationSeconds)
            return false;

        _huntShakeoffs++;
        _huntingSince = Clock.NowMs; // fresh window after the shake-off

        // Abort the relocation the moment monsters appear or the wave truly ends.
        var stop = WaitHelpers.Throttle(
            () => _getNearbyMonsters().Count > 0 || _gameState.MonstersRemaining == 0, 250);

        var tour = _exploration.GetTour();
        if (_huntShakeoffs == 1 && tour is { Count: > 0 })
        {
            var playerPos = _gameState.PlayerGridPos;
            var farPoints = tour.Where(p => Vector2.Distance(playerPos, p) > 60).ToList();
            var pick = farPoints.Count > 0
                ? farPoints[HuntRandom.Next(farPoints.Count)]
                : tour[HuntRandom.Next(tour.Count)];
            _log("[Simulacrum] Hunt is stuck -- relocating to a random spot to reset.");
            await _navigation.MoveToTarget(pick, ct, 20, stopWhen: stop);
            return true;
        }

        // Second strike (or no tour available): walk at most one full lap.
        // Bounded — a monster in an unreachable pocket must not trap the phase
        // machine inside an endless ExploreUntil.
        _log("[Simulacrum] Hunt is still stuck -- walking a full exploration lap.");
        var legs = Math.Max(8, tour?.Count ?? 8);
        for (var i = 0; i < legs && !ct.IsCancellationRequested && !stop(); i++)
        {
            if (!_gameState.IsConnected) break;
            await _exploration.WalkTourLeg(stop, ct);
        }

        return true;
    }

    /// <summary>
    /// Nearest alive pack-spawner daemon anywhere in entity range, skipping ones
    /// recently found unreachable.
    /// </summary>
    public Entity? FindNearestSpawnerDaemon()
    {
        var playerPos = _gameState.PlayerGridPos;
        return _gameState.FindEntitiesByPath(SpawnerDaemonPathFragment)
            .Where(e => e.IsValid && e.IsAlive &&
                        (!_daemonSkipUntil.TryGetValue(e.Id, out var until) || Clock.Reached(until)))
            .OrderBy(e => Vector2.Distance(playerPos, e.GridPosNum))
            .FirstOrDefault();
    }

    /// <summary>
    /// A walk toward the daemon failed with no monsters showing up. Daemons that
    /// couldn't be reached are skipped with an escalating backoff — a genuinely
    /// unreachable one must stop dominating the nearest-first pick.
    /// </summary>
    public void ReportDaemonUnreachable(Entity daemon)
    {
        var fails = _daemonFailCounts.GetValueOrDefault(daemon.Id) + 1;
        _daemonFailCounts[daemon.Id] = fails;
        var skipSeconds = fails switch { 1 => 10, 2 => 30, _ => 60 };
        _daemonSkipUntil[daemon.Id] = Clock.InSeconds(skipSeconds);
        _log($"[Simulacrum] Spawn point unreachable ({fails}x) -- skipping it for {skipSeconds}s.");
    }

    /// <summary>
    /// Last-known position of the nearest alive monster OUTSIDE the network
    /// bubble: Monster-type entries in the full entity list with IsValid=false
    /// but cached IsAlive/IsHostile. The data is a frozen snapshot — a hint of
    /// where to walk, never something to fight. Positions closer than 50 units
    /// are skipped: if we stand there and the monster still isn't streamed in,
    /// the snapshot is stale (wandered off or died out of view).
    /// </summary>
    public Vector2? FindNearestOutOfBubbleMonster()
    {
        var playerPos = _gameState.PlayerGridPos;
        Vector2? best = null;
        var bestDist = float.MaxValue;

        try
        {
            foreach (var e in _gameState.Controller.EntityListWrapper.Entities)
            {
                try
                {
                    if (e == null || e.IsValid || e.Type != EntityType.Monster) continue;
                    if (!e.IsAlive || !e.IsHostile) continue;

                    var path = e.Path;
                    if (path == null || _ignoredPathFragments.Any(path.Contains))
                        continue;

                    var d = Vector2.Distance(playerPos, e.GridPosNum);
                    if (d > 50 && d < bestDist)
                    {
                        bestDist = d;
                        best = e.GridPosNum;
                    }
                }
                catch
                {
                    // Invalid entities carry stale addresses — skip anything
                    // unreadable (expected-common, not worth counting).
                }
            }
        }
        catch (Exception ex)
        {
            // The live entity list can mutate mid-enumeration — just use what we
            // got this tick; the next tick rescans.
            SafeRead.Note("hunt.out-of-bubble", ex);
        }

        return best;
    }

    public void Reset()
    {
        _daemonSkipUntil.Clear();
        _daemonFailCounts.Clear();
        _huntingSince = Clock.Never;
        _huntLastRemaining = 0;
        _huntShakeoffs = 0;
    }
}
