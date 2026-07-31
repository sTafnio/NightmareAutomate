using System;
using System.Numerics;

namespace NightmareAutomate.Helpers;

public static class CoordinateHelpers
{
    /// <summary>Standard click-safety margins (fractions of the window size): points
    /// closer to the edge than this are not trusted as click/hover targets. The Y
    /// margin is larger because of the game's top and bottom UI bars.</summary>
    public const float ClickMarginX = 0.04f;
    public const float ClickMarginY = 0.06f;

    /// <summary>
    /// Clamps a target position to a padded area within screen bounds by projecting
    /// a line from a source point (usually the character) towards the target.
    /// </summary>
    public static Vector2 ClampToScreenWithPadding(Vector2 source, Vector2 target, float screenWidth, float screenHeight, float paddingPercent)
    {
        float xMin = screenWidth * paddingPercent;
        float xMax = screenWidth * (1 - paddingPercent);
        float yMin = screenHeight * paddingPercent;
        float yMax = screenHeight * (1 - paddingPercent);

        // If target is already inside the padded area, return it as is
        if (target.X >= xMin && target.X <= xMax && target.Y >= yMin && target.Y <= yMax)
            return target;

        Vector2 dir = target - source;
        if (dir.LengthSquared() < 0.01f) return target;

        float t = 1.0f;

        // Check intersection with vertical boundaries
        if (dir.X > 0 && target.X > xMax)
            t = Math.Min(t, (xMax - source.X) / dir.X);
        else if (dir.X < 0 && target.X < xMin)
            t = Math.Min(t, (xMin - source.X) / dir.X);

        // Check intersection with horizontal boundaries
        if (dir.Y > 0 && target.Y > yMax)
            t = Math.Min(t, (yMax - source.Y) / dir.Y);
        else if (dir.Y < 0 && target.Y < yMin)
            t = Math.Min(t, (yMin - source.Y) / dir.Y);

        return source + dir * t;
    }

    /// <summary>
    /// Whether a window-relative point lies inside the window with the given margins.
    /// NaN/infinite points never count as inside.
    /// </summary>
    public static bool IsInsideWindow(Vector2 point, float width, float height,
        float marginXPercent, float marginYPercent)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;

        var mx = width * marginXPercent;
        var my = height * marginYPercent;
        return point.X >= mx && point.X <= width - mx &&
               point.Y >= my && point.Y <= height - my;
    }

    private static readonly Random Rng = new();

    /// <summary>
    /// A uniformly random point inside a UI rect, inset from the edges — clicks on
    /// exact centers are a bot tell.
    /// </summary>
    public static Vector2 RandomPointInRect(SharpDX.RectangleF rect, float insetPercent = 0.25f)
    {
        var ix = rect.Width * insetPercent;
        var iy = rect.Height * insetPercent;
        var w = Math.Max(1f, rect.Width - 2 * ix);
        var h = Math.Max(1f, rect.Height - 2 * iy);
        return new Vector2(
            rect.X + ix + (float)Rng.NextDouble() * w,
            rect.Y + iy + (float)Rng.NextDouble() * h);
    }

    /// <summary>
    /// Hard-clamp a window-relative point into the window with the given margins.
    /// Degenerate inputs (NaN/inf from WorldToScreen on behind-camera points) fall
    /// back to the window center. Final safety net for every mouse position.
    /// </summary>
    public static Vector2 HardClampToWindow(Vector2 point, float width, float height,
        float marginXPercent, float marginYPercent)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            return new Vector2(width * 0.5f, height * 0.5f);

        var mx = width * marginXPercent;
        var my = height * marginYPercent;
        return new Vector2(
            Math.Clamp(point.X, mx, width - mx),
            Math.Clamp(point.Y, my, height - my));
    }
}
