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
/// no re-search however far the player deviates between rebuilds) and
/// refreshed every <see cref="RepathIntervalMs"/>.
///
/// Threading: a walk drives its <see cref="RouteTracker"/> from the game thread
/// (the SyncTask pump), so every game read — target, player position, terrain —
/// happens there. Only the flood itself runs on the thread pool, over the
/// terrain array snapshot and a buffer no other walk touches while it runs
/// (same split as ExplorationService's tour build).
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

    // A field over a map grid is several MB. Walks are sequential, so one spare
    // buffer is handed from walk to walk — but only back once no flood can still
    // be writing it (an abandoned walk's flood finishes on its own; the next
    // walk then simply allocates).
    private int[]? _spareBuffer;

    public GridPathfinder(GameController gc) => _gc = gc;

    /// <summary>
    /// Start routing in the current area. Null when the terrain is not readable
    /// (loading, stale host). Dispose the tracker when the walk ends.
    /// </summary>
    public RouteTracker? BeginRoute()
    {
        var terrain = SafeRead.Try("path.terrain", () => _gc.IngameState?.Data?.RawPathfindingData);
        if (terrain == null || terrain.Length == 0) return null;
        return new RouteTracker(this, terrain);
    }

    private int[] TakeBuffer(int size)
    {
        var buffer = _spareBuffer;
        _spareBuffer = null;
        return buffer != null && buffer.Length >= size ? buffer : new int[size];
    }

    private void ReturnBuffer(int[] buffer) => _spareBuffer = buffer;

    /// <summary>
    /// One walk's route toward a live target. Call <see cref="Update"/> once per
    /// frame from the game thread; <see cref="Path"/> holds the latest route
    /// (player → target, one cell per step). <see cref="Finished"/> turns true
    /// when no route exists or the player stands on the target.
    /// </summary>
    public sealed class RouteTracker : IDisposable
    {
        private readonly GridPathfinder _owner;
        private readonly int[][] _terrain;
        private readonly int _rows;
        private readonly int _cols;
        private readonly CancellationTokenSource _floodCts = new();

        private int[]? _dist;
        private Task<bool>? _flood;     // result: the whole reachable component was flooded
        private bool _fieldReady;
        private bool _fullyFlooded;
        private Vector2 _fieldAnchor;
        private long _nextRepathAt;

        internal RouteTracker(GridPathfinder owner, int[][] terrain)
        {
            _owner = owner;
            _terrain = terrain;
            _rows = terrain.Length;
            _cols = terrain[0].Length;
        }

        public List<Vector2i>? Path { get; private set; }
        public bool Finished { get; private set; }

        private bool Walkable(int x, int y) => TerrainGrid.IsWalkable(_terrain, _rows, x, y);

        /// <summary>Advance the route with this frame's target and player cell.</summary>
        public void Update(Vector2 target, Vector2 player)
        {
            if (Finished) return;

            var justFlooded = false;
            if (_flood != null)
            {
                if (!_flood.IsCompleted) return;
                if (!_flood.IsCompletedSuccessfully)
                {
                    // A flood can only fault on a malformed grid — no route.
                    SafeRead.Note("path.flood", _flood.Exception?.GetBaseException()
                                                ?? new OperationCanceledException());
                    Finished = true;
                    return;
                }

                _fullyFlooded = _flood.Result;
                _flood = null;
                _fieldReady = true;
                _nextRepathAt = 0;
                justFlooded = true;
            }

            // A freshly finished field always yields a path first, even if the
            // live target drifted meanwhile: a fast mover could otherwise outrun
            // every flood and starve the walk of paths (leaving only the straight
            // nudge). The drift re-flood then happens on the next update.
            if (!_fieldReady || (!justFlooded && Vector2.Distance(_fieldAnchor, target) > GoalDriftCells))
            {
                var goal = TerrainGrid.Spiral((int)target.X, (int)target.Y, _rows, _cols, SnapRadius, Walkable);
                if (goal == null)
                {
                    Finished = true;
                    return;
                }

                _fieldAnchor = target;
                _fieldReady = false;
                var start = TerrainGrid.Spiral((int)player.X, (int)player.Y, _rows, _cols, SnapRadius, Walkable);
                var dist = _dist ??= _owner.TakeBuffer(_rows * _cols);
                var (terrain, rows, cols, token) = (_terrain, _rows, _cols, _floodCts.Token);
                _flood = Task.Run(
                    () => BuildDistanceField(terrain, rows, cols, goal.Value, start, dist, token), token);
                return;
            }

            if (!Clock.Reached(_nextRepathAt)) return;
            _nextRepathAt = Clock.InMs(RepathIntervalMs);

            var path = ExtractPath(_dist!, _rows, _cols, (int)player.X, (int)player.Y);
            if (path == null)
            {
                // Player outside the flooded neighborhood — widen by re-flooding
                // around the current position. If the field already covers the
                // whole reachable component, there is genuinely no route.
                if (_fullyFlooded)
                    Finished = true;
                else
                    _fieldReady = false;
                return;
            }

            Path = path;
            if (path.Count == 0)
                Finished = true; // standing on the target
        }

        public void Dispose()
        {
            _floodCts.Cancel();
            if (_dist != null && _flood is null or { IsCompleted: true })
                _owner.ReturnBuffer(_dist);
            _dist = null;
        }
    }

    /// <summary>
    /// Dijkstra flood outward from the goal. 8-way with 10/14 costs; diagonals
    /// require both adjacent cardinals walkable so paths never cut corners.
    /// Expansion stops early once the start cell settles (plus slack) — the
    /// early exit is safe for descent because every cell at distance ≤ budget
    /// is fully settled when we stop. Abandoned walks cancel it (checked every
    /// few thousand cells), so it never outlives its walk by much.
    /// </summary>
    private static bool BuildDistanceField(
        int[][] terrain, int rows, int cols, Vector2i goal, Vector2i? start, int[] dist, CancellationToken ct)
    {
        var size = rows * cols;
        Array.Fill(dist, Unreached, 0, size);

        bool Walkable(int x, int y) => TerrainGrid.IsWalkable(terrain, rows, x, y);

        var open = new PriorityQueue<int, int>();
        var goalIdx = goal.Y * cols + goal.X;
        dist[goalIdx] = 0;
        open.Enqueue(goalIdx, 0);

        var startIdx = start.HasValue ? start.Value.Y * cols + start.Value.X : -1;
        var budget = long.MaxValue;
        var expanded = 0;

        while (open.TryDequeue(out var idx, out var d))
        {
            if ((++expanded & 4095) == 0)
                ct.ThrowIfCancellationRequested();
            if (d != dist[idx]) continue; // stale queue entry
            if (d > budget) return false;
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

        return true;
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
