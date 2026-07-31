using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ExileCore;
using GameOffsets.Native;
using NightmareAutomate.Helpers;

namespace NightmareAutomate.Services;

/// <summary>
/// In-plugin routing over the game's own walkability grid
/// (IngameState.Data.RawPathfindingData: 0 = blocked, 1-5 walkable).
/// Two key properties:
///
/// 1. The target is LIVE: it is re-read every repath tick, so a monster that
///    repositions mid-walk is followed instead of walked-to-where-it-was.
/// 2. The Dijkstra flood from the target stops expanding once the player's
///    cell is covered (plus slack), so engaging a monster 50 cells away floods
///    a neighborhood rather than the whole map — cheap enough to rebuild
///    whenever the target drifts or the player strays outside coverage.
///
/// Paths are extracted from the field by steepest descent (O(path length),
/// no re-search however far the player deviates between rebuilds) and streamed
/// through the callback every <see cref="RepathIntervalMs"/>.
/// </summary>
public sealed class GridPathfinder
{
    private const int RepathIntervalMs = 150;
    private const int SnapRadius = 25;      // spiral search for a usable start/goal cell
    private const int CardinalCost = 10;
    private const int DiagonalCost = 14;
    private const int Unreached = int.MaxValue;

    // Rebuild the field once the live target has drifted this many cells from
    // the field's anchor; smaller drift is absorbed by the callers' own stop
    // conditions (hover range, stopWhen).
    private const int GoalDriftCells = 6;

    // Early-exit slack: after the player's cell settles at distance D, keep
    // flooding until D * factor + slack so moderate straying stays covered.
    private const float CoverageSlackFactor = 1.4f;
    private const int CoverageSlackCells = 40;

    private readonly GameController _gc;

    // A field over a map grid is several MB; walks are strictly sequential
    // (NavigationService awaits the routing task before returning), so a single
    // reused buffer avoids per-walk allocation churn.
    private int[] _dist = [];

    public GridPathfinder(GameController gc) => _gc = gc;

    /// <summary>
    /// Runs until cancelled, repeatedly invoking the callback with the remaining
    /// path (player → target, one Vector2i per grid cell). Completes on its own
    /// when no route exists or the player is standing on the target.
    /// </summary>
    public async Task LookForRoute(Func<Vector2> getTarget, Action<List<Vector2i>> onPathFound, CancellationToken ct)
    {
        int[][]? terrain;
        try
        {
            terrain = _gc.IngameState?.Data?.RawPathfindingData;
        }
        catch
        {
            terrain = null;
        }

        if (terrain == null || terrain.Length == 0) return;

        var rows = terrain.Length;
        var cols = terrain[0].Length;

        bool Walkable(int x, int y) => TerrainGrid.IsWalkable(terrain, rows, x, y);

        int[]? dist = null;
        var fullyFlooded = false;
        var fieldAnchor = Vector2.Zero;

        while (!ct.IsCancellationRequested)
        {
            var targetNow = getTarget();
            var playerPos = _gc.Player?.GridPosNum ?? Vector2.Zero;

            if (dist == null || Vector2.Distance(fieldAnchor, targetNow) > GoalDriftCells)
            {
                var goal = TerrainGrid.Spiral((int)targetNow.X, (int)targetNow.Y, rows, cols, SnapRadius, Walkable);
                if (goal == null) return;
                fieldAnchor = targetNow;

                var start = TerrainGrid.Spiral((int)playerPos.X, (int)playerPos.Y, rows, cols, SnapRadius, Walkable);
                (dist, fullyFlooded) = await Task.Run(
                    () => BuildDistanceField(terrain, rows, cols, goal.Value, start), ct);
            }

            var path = ExtractPath(dist, rows, cols, (int)playerPos.X, (int)playerPos.Y);
            if (path == null)
            {
                // Player outside the flooded neighborhood — widen by re-flooding
                // around the current position. If the field already covers the
                // whole reachable component, there is genuinely no route.
                if (fullyFlooded) return;
                dist = null;
                continue;
            }

            if (path.Count == 0) return; // standing on the target
            onPathFound(path);

            await Task.Delay(RepathIntervalMs, ct);
        }
    }

    /// <summary>
    /// Dijkstra flood outward from the goal. 8-way with 10/14 costs; diagonals
    /// require both adjacent cardinals walkable so paths never cut corners.
    /// Expansion stops early once the start cell settles (plus slack) — the
    /// early exit is safe for descent because every cell at distance ≤ budget
    /// is fully settled when we stop.
    /// </summary>
    private (int[] Dist, bool FullyFlooded) BuildDistanceField(
        int[][] terrain, int rows, int cols, Vector2i goal, Vector2i? start)
    {
        var size = rows * cols;
        if (_dist.Length < size) _dist = new int[size];
        var dist = _dist;
        Array.Fill(dist, Unreached, 0, size);

        bool Walkable(int x, int y) => TerrainGrid.IsWalkable(terrain, rows, x, y);

        var open = new PriorityQueue<int, int>();
        var goalIdx = goal.Y * cols + goal.X;
        dist[goalIdx] = 0;
        open.Enqueue(goalIdx, 0);

        var startIdx = start.HasValue ? start.Value.Y * cols + start.Value.X : -1;
        var budget = long.MaxValue;

        while (open.TryDequeue(out var idx, out var d))
        {
            if (d != dist[idx]) continue; // stale queue entry
            if (d > budget) return (dist, false);
            if (idx == startIdx)
                budget = (long)(d * CoverageSlackFactor) + CoverageSlackCells * CardinalCost;

            var x = idx % cols;
            var y = idx / cols;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (!Walkable(nx, ny)) continue;
                    if (dx != 0 && dy != 0 && (!Walkable(x + dx, y) || !Walkable(x, y + dy)))
                        continue;

                    var nIdx = ny * cols + nx;
                    var nd = d + (dx != 0 && dy != 0 ? DiagonalCost : CardinalCost);
                    if (nd >= dist[nIdx]) continue;
                    dist[nIdx] = nd;
                    open.Enqueue(nIdx, nd);
                }
            }
        }

        return (dist, true);
    }

    /// <summary>
    /// Steepest descent from the player cell down the distance field to the goal
    /// (distance 0). Within the settled region every non-goal cell has a strictly
    /// cheaper neighbor, so this terminates; landing on an unsettled fringe cell
    /// (possible after an early-exited flood) returns null so the caller
    /// re-floods. Unreached cells double as the walkability mask, which keeps
    /// the corner-cut rule intact during descent.
    /// </summary>
    private static List<Vector2i>? ExtractPath(int[] dist, int rows, int cols, int px, int py)
    {
        // The player can momentarily stand on a blocked/unreached cell (doodads,
        // transition landings) — snap to the nearest cell the flood covered.
        var start = TerrainGrid.Spiral(px, py, rows, cols, SnapRadius, (x, y) => dist[y * cols + x] != Unreached);
        if (start == null) return null;

        var path = new List<Vector2i>(64);
        var (x, y) = (start.Value.X, start.Value.Y);
        while (dist[y * cols + x] > 0)
        {
            var best = dist[y * cols + x];
            int bestX = x, bestY = y;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                    if (dx != 0 && dy != 0 &&
                        (dist[y * cols + nx] == Unreached || dist[ny * cols + x] == Unreached))
                        continue;

                    var nd = dist[ny * cols + nx];
                    if (nd < best)
                    {
                        best = nd;
                        bestX = nx;
                        bestY = ny;
                    }
                }
            }

            if (bestX == x && bestY == y) return null; // unsettled fringe cell
            x = bestX;
            y = bestY;
            path.Add(new Vector2i(x, y));
        }

        return path;
    }
}
