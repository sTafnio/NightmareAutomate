using System;
using GameOffsets.Native;

namespace NightmareAutomate.Helpers;

/// <summary>
/// Shared conventions for the game's walkability grid
/// (IngameState.Data.RawPathfindingData): 0 = blocked, 1-5 walkable, 4-5 open ground.
/// Single home for the walkable check and the ring search so GridPathfinder and
/// ExplorationService cannot drift apart.
/// </summary>
public static class TerrainGrid
{
    public const int MinWalkableValue = 1;
    public const int OpenGroundValue = 4;

    /// <summary>Bounds-safe walkability test over a jagged terrain grid.</summary>
    public static bool IsWalkable(int[][] terrain, int rows, int x, int y) =>
        y >= 0 && y < rows && x >= 0 && terrain[y] != null && x < terrain[y].Length &&
        terrain[y][x] >= MinWalkableValue;

    /// <summary>
    /// Nearest cell satisfying the predicate, searched in expanding Chebyshev
    /// rings up to maxRadius. The seed is clamped into bounds first.
    /// </summary>
    public static Vector2i? Spiral(int x, int y, int rows, int cols, int maxRadius, Func<int, int, bool> ok)
    {
        x = Math.Clamp(x, 0, cols - 1);
        y = Math.Clamp(y, 0, rows - 1);
        if (ok(x, y)) return new Vector2i(x, y);

        for (var r = 1; r <= maxRadius; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < cols && ny < rows && ok(nx, ny))
                        return new Vector2i(nx, ny);
                }
            }
        }

        return null;
    }
}
