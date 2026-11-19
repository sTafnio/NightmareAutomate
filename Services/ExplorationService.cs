using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ExileCore.Shared;
using NightmareAutomate.Helpers;

namespace NightmareAutomate.Services;

/// <summary>
/// Auto-generates an exploration tour of the current area from the game's own
/// walkability grid (IngameState.Data.RawPathfindingData: 0 = blocked, 1-5 walkable,
/// 4-5 = open ground) — the same data GridPathfinder routes over.
///
/// Flood-fills the reachable region from the player, drops a point into every
/// sufficiently open grid cell, and orders the points angularly around the region's
/// centroid — producing a loop that loosely follows the area's outer edges.
/// Replaces hand-recorded patrol points; generation runs once per area on a
/// background thread.
/// </summary>
public sealed class ExplorationService
{
    private const int CellSize = 35;
    private const int MinOpenTilesPerCell = 120;

    /// <summary>An empty/failed build is retried after this long — terrain can
    /// stream in late, and caching an empty tour forever would make
    /// <see cref="ExploreUntil"/> hang silently.</summary>
    private const int EmptyTourRetrySeconds = 10;

    private readonly GameStateService _gameState;
    private readonly NavigationService _navigation;
    private readonly Action<string> _log;

    /// <summary>Keyed by the area INSTANCE hash, not the name — two instances of a
    /// same-named area (random map layouts) must never share a tour.</summary>
    private uint _builtForAreaHash;
    private long _builtAt = Clock.Never;
    private IReadOnlyList<Vector2>? _tour;
    private Task<IReadOnlyList<Vector2>>? _buildTask;
    private int _tourIndex;

    /// <summary>Walks toward the current tour point that ended short of it. A
    /// point with no route (or one the walk keeps stalling on) never comes within
    /// arrival range — without this it would be retried forever.</summary>
    private int _failedLegs;
    private const int MaxFailedLegsPerPoint = 2;

    public ExplorationService(GameStateService gameState, NavigationService navigation, Action<string> log)
    {
        _gameState = gameState;
        _navigation = navigation;
        _log = log;
    }

    /// <summary>
    /// Walk ONE leg of the tour toward the current tour point, advancing to the
    /// next point on arrival and aborting the leg early when <paramref name="found"/>
    /// becomes true. Non-blocking building block for discovery searches — call it
    /// once per phase tick, or loop it via <see cref="ExploreUntil"/>.
    /// Waits briefly and returns when the tour is still being generated.
    /// </summary>
    public async SyncTask<bool> WalkTourLeg(Func<bool> found, CancellationToken ct)
    {
        var tour = GetTour();
        if (tour == null || tour.Count == 0)
        {
            await Task.Delay(500, ct); // tour still generating
            return false;
        }

        var point = tour[_tourIndex % tour.Count];
        if (Vector2.Distance(_gameState.PlayerGridPos, point) < 25)
        {
            _tourIndex++;
            _failedLegs = 0;
            return true;
        }

        var arrived = await _navigation.MoveToTarget(point, ct, 20, stopWhen: found);
        if (arrived || found())
        {
            _failedLegs = 0;
        }
        else if (++_failedLegs >= MaxFailedLegsPerPoint)
        {
            _log($"[Explore] Tour point {point} is unreachable -- skipping it.");
            _tourIndex++;
            _failedLegs = 0;
        }

        return true;
    }

    /// <summary>
    /// Walk the tour until the condition holds. No give-up timer — used for
    /// things that always exist in the area (initiator, stash, return portal).
    /// Bails on disconnect: this loop runs INSIDE a state handler, and the
    /// engine's disconnect recovery only gets a turn between handler steps.
    /// </summary>
    public async SyncTask<bool> ExploreUntil(Func<bool> found, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _gameState.IsConnected && !found())
            await WalkTourLeg(found, ct);
        return found();
    }

    /// <summary>
    /// The exploration tour for the current area INSTANCE, or null while it is
    /// still being generated (kicks off generation on first call per instance).
    /// Empty and failed builds are retried after a backoff instead of being
    /// cached forever.
    /// </summary>
    public IReadOnlyList<Vector2>? GetTour()
    {
        var areaHash = _gameState.AreaHash;
        if (areaHash == 0) return null;

        var needsBuild =
            _builtForAreaHash != areaHash ||
            (_buildTask == null && _tour is { Count: 0 } &&
             Clock.SecondsSince(_builtAt) > EmptyTourRetrySeconds);

        if (needsBuild)
        {
            var terrain = SafeRead.Try("explore.terrain",
                () => _gameState.Controller.IngameState?.Data?.RawPathfindingData);

            if (terrain == null || terrain.Length == 0)
                return null;

            _builtForAreaHash = areaHash;
            _builtAt = Clock.NowMs;
            _tour = null;
            _tourIndex = 0;
            _failedLegs = 0;
            var seed = _gameState.PlayerGridPos;
            _buildTask = Task.Run(() => Build(terrain, seed));
        }

        if (_buildTask is { IsCompletedSuccessfully: true })
        {
            _tour = _buildTask.Result;
            _buildTask = null;
            _builtAt = Clock.NowMs;
            if (_tour.Count == 0)
                _log($"[Explore] Tour generation found no open ground in '{_gameState.AreaName}' -- retrying in {EmptyTourRetrySeconds}s.");
            else
                _log($"[Explore] Generated {_tour.Count} exploration points for '{_gameState.AreaName}'.");
        }
        else if (_buildTask is { IsFaulted: true })
        {
            _log($"[Explore] Tour generation failed: {_buildTask.Exception?.GetBaseException().Message}");
            _buildTask = null;
            _builtAt = Clock.NowMs;
            _tour = []; // empty -> falls into the retry backoff above
        }

        return _tour;
    }

    private static IReadOnlyList<Vector2> Build(int[][] terrain, Vector2 seed)
    {
        var rows = terrain.Length;
        var cols = terrain[0].Length;

        bool Walkable(int x, int y) => TerrainGrid.IsWalkable(terrain, rows, x, y);

        // Seed must be a walkable tile — spiral out a little if it isn't.
        var snapped = TerrainGrid.Spiral((int)seed.X, (int)seed.Y, rows, cols, 20, Walkable);
        if (snapped == null) return [];
        var (sx, sy) = (snapped.Value.X, snapped.Value.Y);

        // Flood fill the reachable region.
        var reachable = new bool[rows * cols];
        var queue = new Queue<(int X, int Y)>();
        reachable[sy * cols + sx] = true;
        queue.Enqueue((sx, sy));

        Span<(int dx, int dy)> neighbors = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (dx, dy) in neighbors)
            {
                int nx = x + dx, ny = y + dy;
                if (!Walkable(nx, ny)) continue;
                var idx = ny * cols + nx;
                if (reachable[idx]) continue;
                reachable[idx] = true;
                queue.Enqueue((nx, ny));
            }
        }

        // One point per sufficiently open cell: the open-ground tile nearest the
        // cell center.
        var points = new List<Vector2>();
        for (var cy = 0; cy < rows; cy += CellSize)
        {
            for (var cx = 0; cx < cols; cx += CellSize)
            {
                var centerX = cx + CellSize / 2f;
                var centerY = cy + CellSize / 2f;
                var openCount = 0;
                Vector2? best = null;
                var bestDist = float.MaxValue;

                var yEnd = Math.Min(rows, cy + CellSize);
                for (var y = cy; y < yEnd; y++)
                {
                    var row = terrain[y];
                    if (row == null) continue;
                    var xEnd = Math.Min(row.Length, cx + CellSize);
                    for (var x = cx; x < xEnd; x++)
                    {
                        if (!reachable[y * cols + x] || row[x] < TerrainGrid.OpenGroundValue) continue;
                        openCount++;
                        var d = (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = new Vector2(x, y);
                        }
                    }
                }

                if (openCount >= MinOpenTilesPerCell && best != null)
                    points.Add(best.Value);
            }
        }

        if (points.Count == 0) return [];
        if (points.Count <= 2) return points;

        // Order the points into a layout-adaptive tour: minimum spanning tree over
        // TRUE walkable distances (BFS on a downsampled grid — straight-line distance
        // would pair up points separated by walls), walked depth-first. Ring layouts
        // become a lap; layouts with arms get natural in-and-out excursions.
        var seedVec = new Vector2(sx, sy);
        var distances = ComputePairwiseWalkDistances(terrain, reachable, rows, cols, points);
        var order = MstDepthFirstOrder(points, distances, seedVec);
        return order.Select(i => points[i]).ToList();
    }

    /// <summary>
    /// Pairwise walk distances between points via per-point BFS on a 4x downsampled
    /// walkability grid. Unreachable pairs (downsampling can pinch narrow paths)
    /// fall back to penalized straight-line distance.
    /// </summary>
    private static float[,] ComputePairwiseWalkDistances(
        int[][] terrain, bool[] reachable, int rows, int cols, List<Vector2> points)
    {
        const int ds = 4;
        var dsRows = (rows + ds - 1) / ds;
        var dsCols = (cols + ds - 1) / ds;

        // A downsampled block is walkable when any of its reachable tiles is.
        var walkable = new bool[dsRows * dsCols];
        for (var y = 0; y < rows; y++)
        {
            var row = terrain[y];
            if (row == null) continue;
            for (var x = 0; x < row.Length && x < cols; x++)
            {
                if (reachable[y * cols + x])
                    walkable[(y / ds) * dsCols + (x / ds)] = true;
            }
        }

        var n = points.Count;
        var result = new float[n, n];
        var pointCells = points
            .Select(p => ((int)p.X / ds, (int)p.Y / ds))
            .ToArray();

        var dist = new int[dsRows * dsCols];
        var queue = new Queue<(int X, int Y)>();
        (int dx, int dy)[] neighbors = [(1, 0), (-1, 0), (0, 1), (0, -1)];

        for (var i = 0; i < n; i++)
        {
            Array.Fill(dist, -1);
            queue.Clear();
            var (px, py) = pointCells[i];
            dist[py * dsCols + px] = 0;
            queue.Enqueue((px, py));

            while (queue.Count > 0)
            {
                var (x, y) = queue.Dequeue();
                var d = dist[y * dsCols + x];
                foreach (var (dx, dy) in neighbors)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= dsCols || ny >= dsRows) continue;
                    var idx = ny * dsCols + nx;
                    if (!walkable[idx] || dist[idx] >= 0) continue;
                    dist[idx] = d + 1;
                    queue.Enqueue((nx, ny));
                }
            }

            for (var j = 0; j < n; j++)
            {
                var (qx, qy) = pointCells[j];
                var d = dist[qy * dsCols + qx];
                result[i, j] = d >= 0
                    ? d * ds
                    : Vector2.Distance(points[i], points[j]) * 3f; // penalized fallback
            }
        }

        return result;
    }

    /// <summary>
    /// Prim's MST over the distance matrix, then a depth-first preorder walk starting
    /// at the point closest to the seed — the tour order.
    /// </summary>
    private static List<int> MstDepthFirstOrder(List<Vector2> points, float[,] distances, Vector2 seed)
    {
        var n = points.Count;
        var start = 0;
        var bestSeedDist = float.MaxValue;
        for (var i = 0; i < n; i++)
        {
            var d = Vector2.DistanceSquared(points[i], seed);
            if (d < bestSeedDist)
            {
                bestSeedDist = d;
                start = i;
            }
        }

        // Prim's algorithm.
        var inTree = new bool[n];
        var parent = new int[n];
        var best = new float[n];
        Array.Fill(best, float.MaxValue);
        Array.Fill(parent, -1);
        best[start] = 0;

        for (var step = 0; step < n; step++)
        {
            var u = -1;
            var uBest = float.MaxValue;
            for (var i = 0; i < n; i++)
            {
                if (!inTree[i] && best[i] < uBest)
                {
                    uBest = best[i];
                    u = i;
                }
            }

            if (u < 0) break;
            inTree[u] = true;

            for (var v = 0; v < n; v++)
            {
                if (!inTree[v] && distances[u, v] < best[v])
                {
                    best[v] = distances[u, v];
                    parent[v] = u;
                }
            }
        }

        var children = new List<int>[n];
        for (var i = 0; i < n; i++) children[i] = [];
        for (var i = 0; i < n; i++)
        {
            if (parent[i] >= 0)
                children[parent[i]].Add(i);
        }

        // DFS preorder; visit nearer children first for tighter excursions.
        var order = new List<int>(n);
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var u = stack.Pop();
            order.Add(u);
            foreach (var c in children[u].OrderByDescending(c => distances[u, c]))
                stack.Push(c);
        }

        return order;
    }
}
