using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using GameOffsets.Native;
using NightmareAutomate.Helpers;

namespace NightmareAutomate.Services;

/// <summary>
/// Handles all player movement — hold-to-move path following and entity clicking.
/// </summary>
public sealed class NavigationService
{
    /// <summary>
    /// One approach distance (grid units) for ALL world interactables — stash, NPCs,
    /// portals, map device. Chosen close (matches the old stash behavior): panels can
    /// draw over far-away interactables and swallow the click; standing next to the
    /// target changes the projection away from fixed UI. Walks stop at 0.8× this.
    /// </summary>
    public const float InteractDistance = 15f;

    /// <summary>Steering this long with no ground gained abandons the walk — a
    /// stuck route or (the case this guards) a disconnect freeze where the client
    /// keeps rendering the last snapshot with InGame still true.</summary>
    private const int StallAbortMs = 4000;

    /// <summary>How far (grid units) the corner-unstick sidestep tries to move.</summary>
    private const float SidestepDistance = 18f;

    private static readonly Random SidestepRandom = new();

    private readonly GameStateService _gameState;
    private readonly GridPathfinder _pathfinding;
    private readonly InputService _input;
    private readonly Action<string> _log;
    private readonly Action<string> _chatter;
    private readonly Func<int> _getLookahead;
    private readonly Func<Keys> _getMoveKey;

    private DateTime _lastLogTime = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _throttledLogAt = [];

    /// <summary>
    /// Per-key 1s log throttle. Walks that end instantly (already arrived, no
    /// route, stop condition true on entry) get re-issued by per-frame caller
    /// loops — unthrottled entry/exit logs would flood hundreds of lines per
    /// second in those loops.
    /// </summary>
    private void LogThrottled(string key, string message)
    {
        if (_throttledLogAt.TryGetValue(key, out var at) && (DateTime.Now - at).TotalSeconds < 1)
            return;
        _throttledLogAt[key] = DateTime.Now;
        _log(message);
    }

    /// <param name="log">Important events — goes to the overlay AND the file log.</param>
    /// <param name="chatter">Per-second steering noise ("Moving towards ...") —
    /// overlay only, so an overnight session does not flood the file log.</param>
    public NavigationService(
        GameStateService gameState,
        GridPathfinder pathfinding,
        InputService input,
        Action<string> log,
        Action<string> chatter,
        Func<int> getLookahead,
        Func<Keys> getMoveKey)
    {
        _gameState = gameState;
        _pathfinding = pathfinding;
        _input = input;
        _log = log;
        _chatter = chatter;
        _getLookahead = getLookahead;
        _getMoveKey = getMoveKey;
    }

    /// <summary>
    /// Move the player to a target grid position using the in-plugin grid pathfinding
    /// (GridPathfinder) and hold-to-move.
    /// Returns true if the player arrived within stopDistance.
    ///
    /// pathArrival: judge arrival by REMAINING PATH LENGTH instead of straight-line
    /// distance. Essential when the target is euclidean-close but behind a wall or on
    /// another level — a straight-line check would claim "arrived" without moving.
    /// </summary>
    public SyncTask<bool> MoveToTarget(
        Vector2 targetPos, CancellationToken ct, float stopDistance = 15,
        bool pathArrival = false, bool keepMoveKeyHeld = false, Func<bool>? stopWhen = null)
        => MoveToTarget(() => targetPos, ct, stopDistance, pathArrival, keepMoveKeyHeld, stopWhen);

    /// <summary>
    /// Live-target variant: the destination is re-read every tick and the route
    /// follows it, so a monster that repositions mid-walk is chased rather than
    /// walked-to-where-it-was.
    /// </summary>
    public async SyncTask<bool> MoveToTarget(
        Func<Vector2> target, CancellationToken ct, float stopDistance = 15,
        bool pathArrival = false, bool keepMoveKeyHeld = false, Func<bool>? stopWhen = null)
    {
        var stoppedByCondition = false;
        try
        {
            List<Vector2i>? currentActivePath = null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var routingTask = _pathfinding.LookForRoute(target, updatedPath =>
            {
                currentActivePath = updatedPath;
            }, cts.Token);

            var requestedAt = DateTime.Now;

            // No-progress watchdog: while we are actively steering, the character
            // must keep making ground. A stall means stuck geometry OR — the case
            // this exists for — a disconnect freeze, where the client renders the
            // last server snapshot (InGame stays TRUE) so no connection flag can
            // catch it. When steering stops advancing the player for this long, the
            // walk is abandoned so control returns to the engine (whose recovery
            // fires once PoE finally drops the frozen session and flips InGame).
            var lastProgressPos = _gameState.PlayerGridPos;
            var lastProgressAt = DateTime.Now;
            var sidestepTried = false;

            try
            {
                while (!routingTask.IsCompleted && !cts.IsCancellationRequested)
                {
                    // Clean drop already registered (InGame false) — bail instantly.
                    if (!_gameState.IsConnected)
                    {
                        cts.Cancel();
                        break;
                    }

                    // External satisfaction condition (e.g. "target now in sight and
                    // in range") — the walk's purpose can complete before arrival.
                    if (stopWhen?.Invoke() == true)
                    {
                        stoppedByCondition = true;
                        cts.Cancel();
                        break;
                    }

                    var playerGridPos = _gameState.PlayerGridPos;
                    var targetPos = target();
                    var arrivedNow = pathArrival
                        ? currentActivePath is { Count: > 0 } && currentActivePath.Count <= (int)stopDistance
                        : Vector2.Distance(playerGridPos, targetPos) < stopDistance;
                    if (arrivedNow)
                    {
                        cts.Cancel();
                        break;
                    }

                    var steered = false;
                    if (currentActivePath is { Count: > 0 })
                    {
                        var pointAhead = Math.Min(currentActivePath.Count - 1, _getLookahead());
                        var targetNode = currentActivePath[pointAhead];
                        await SteerTowards(new Vector2(targetNode.X, targetNode.Y), ct, logNode: targetNode.ToString());
                        steered = true;
                    }
                    else if (!pathArrival && (DateTime.Now - requestedAt).TotalMilliseconds > 700)
                    {
                        // The first path can lag (distance field still building on a
                        // fresh area, terrain not exposed yet). Nudge directly toward
                        // the target until it arrives — but never in pathArrival mode:
                        // those walks exist precisely because the straight line is
                        // blocked (wall between us), so a nudge would walk into it.
                        await SteerTowards(targetPos, ct, logNode: "nudge (no path yet)");
                        steered = true;
                    }

                    // Watchdog accounting: reset on real ground gained; only accrue
                    // stall time while actually steering (path-building ticks, where
                    // we cannot move yet, must not count as a stall).
                    if (Vector2.Distance(playerGridPos, lastProgressPos) > 3)
                    {
                        lastProgressPos = playerGridPos;
                        lastProgressAt = DateTime.Now;
                    }
                    else if (steered && (DateTime.Now - lastProgressAt).TotalMilliseconds > StallAbortMs)
                    {
                        // One sidestep before giving up: a short walk in a random
                        // walkable direction un-wedges corner geometry (the classic
                        // stuck case). Ground gained resets the watchdog and the
                        // route recomputes from the new spot; no ground gained
                        // (hard wedge or frozen client) abandons as before.
                        var unstuck = false;
                        if (!sidestepTried)
                        {
                            sidestepTried = true;
                            LogThrottled("nav-sidestep",
                                "No movement progress -- sidestepping to get unstuck.");
                            unstuck = await TrySidestep(ct);
                        }

                        if (unstuck)
                        {
                            lastProgressPos = _gameState.PlayerGridPos;
                            lastProgressAt = DateTime.Now;
                        }
                        else
                        {
                            LogThrottled("nav-stall",
                                "No movement progress -- abandoning the walk (stuck or frozen/disconnected).");
                            cts.Cancel();
                            break;
                        }
                    }

                    // Unconditional yield: without a path (field still building or no
                    // route exists) this loop would otherwise spin the game thread.
                    await TaskUtils.NextFrame();
                }
            }
            finally
            {
                if (!keepMoveKeyHeld)
                    await ReleaseMoveKey();
            }

            try { await routingTask; }
            catch (OperationCanceledException) { /* Expected on arrival */ }

            var arrived = stoppedByCondition ||
                          Vector2.Distance(_gameState.PlayerGridPos, target()) < stopDistance * (pathArrival ? 2f : 1.5f);
            // Successful walks are routine — only failures are worth a log line.
            if (!arrived)
                LogThrottled("nav-failed", "Navigation finished without arriving (no route / not reached).");
            return arrived;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log($"Error in NavigationService: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Corner-unstick maneuver: steer ~18 units toward a random walkable direction
    /// for up to ~1.2s. Probes 8 directions (midpoint + endpoint against the
    /// walkability grid) starting from a random angle. True = real ground was
    /// gained; false = hard-wedged or a frozen client, and the caller should
    /// abandon the walk as before.
    /// </summary>
    private async SyncTask<bool> TrySidestep(CancellationToken ct)
    {
        int[][]? terrain;
        try
        {
            terrain = _gameState.Controller.IngameState?.Data?.RawPathfindingData;
        }
        catch
        {
            terrain = null;
        }

        var start = _gameState.PlayerGridPos;
        var rows = terrain?.Length ?? 0;

        var baseAngle = SidestepRandom.NextDouble() * Math.PI * 2;
        Vector2? dest = null;
        for (var i = 0; i < 8; i++)
        {
            var angle = baseAngle + i * (Math.PI / 4);
            var dir = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            var mid = start + dir * (SidestepDistance / 2);
            var end = start + dir * SidestepDistance;
            if (terrain == null ||
                (TerrainGrid.IsWalkable(terrain, rows, (int)mid.X, (int)mid.Y) &&
                 TerrainGrid.IsWalkable(terrain, rows, (int)end.X, (int)end.Y)))
            {
                dest = end;
                break;
            }
        }

        if (dest == null) return false;

        var deadline = DateTime.Now.AddMilliseconds(1200);
        while (DateTime.Now < deadline && !ct.IsCancellationRequested)
        {
            if (Vector2.Distance(_gameState.PlayerGridPos, start) > SidestepDistance * 0.6f)
                return true;
            await SteerTowards(dest.Value, ct, logNode: "sidestep");
            await TaskUtils.NextFrame();
        }

        return Vector2.Distance(_gameState.PlayerGridPos, start) > 5;
    }

    /// <summary>
    /// One steering step: point the (throttled) cursor toward the grid position,
    /// clamped to the screen, and make sure the move key is held.
    /// </summary>
    private async SyncTask<bool> SteerTowards(Vector2 targetGridPos, CancellationToken ct, string? logNode = null)
    {
        var screenPos = ConvertGridToScreen(new Vector2i((int)targetGridPos.X, (int)targetGridPos.Y));
        var windowRect = _gameState.WindowRect;
        var playerScreenCoord = _gameState.WorldToScreen(_gameState.PlayerWorldPos);

        var playerScreen = new Vector2(playerScreenCoord.X, playerScreenCoord.Y);
        var clampedScreenPos = CoordinateHelpers.ClampToScreenWithPadding(
            playerScreen, screenPos, windowRect.Width, windowRect.Height, 0.10f);

        // The game ignores move commands too close to the character (dead zone).
        // A short path that curls around a wall corner can put the steering node
        // only a couple of cells away euclidean — push the cursor outward along
        // the same direction so hold-to-move never stalls.
        var minCursorDistance = windowRect.Height * 0.08f;
        var offset = clampedScreenPos - playerScreen;
        var offsetLen = offset.Length();
        if (offsetLen > 0.5f && offsetLen < minCursorDistance)
            clampedScreenPos = playerScreen + offset * (minCursorDistance / offsetLen);

        // The line projection can still escape when the player's own screen position
        // is off (transitions, NaN from WorldToScreen) — hard-clamp as a final net.
        clampedScreenPos = CoordinateHelpers.HardClampToWindow(
            clampedScreenPos, windowRect.Width, windowRect.Height, 0.10f, 0.10f);

        var clickPos = new Vector2(
            clampedScreenPos.X + windowRect.Left,
            clampedScreenPos.Y + windowRect.Top);

        if (logNode != null && (DateTime.Now - _lastLogTime).TotalMilliseconds > 1000)
        {
            _chatter($"Moving towards {logNode}");
            _lastLogTime = DateTime.Now;
        }

        var moveKey = _getMoveKey();
        await _input.HoverMouse(clickPos, ct);
        if (!InputService.IsKeyDown(moveKey))
            await _input.KeyDown(moveKey, ct);
        return true;
    }

    /// <summary>
    /// Click an interactable entity (portal, stash, map device, NPC): move near
    /// it, wait for the character to stand still, hover its interact center,
    /// frame-check until its Targetable component reports isTargeted, then click.
    /// Does not verify the interaction succeeded — the caller checks the resulting
    /// state (window opened, area changed, wave started, ...).
    /// </summary>
    public async SyncTask<bool> ClickEntity(
        Entity target, CancellationToken ct, float approachDistance = InteractDistance)
    {
        if (Vector2.Distance(_gameState.PlayerGridPos, target.GridPosNum) > approachDistance)
        {
            _log($"Target entity too far, moving closer ({target.Path})...");
            await MoveToTarget(target.GridPosNum, ct, approachDistance * 0.8f);

            // The walk can fail outright (no route). Clicking from across the map
            // would only auto-walk the character into the unknown — bail so the
            // caller's retry logic re-evaluates instead.
            if (Vector2.Distance(_gameState.PlayerGridPos, target.GridPosNum) > approachDistance * 1.5f)
            {
                _log($"Could not get near the target entity, not clicking ({target.Path}).");
                return false;
            }
        }

        await ReleaseMoveKey();
        await WaitForStandstill(ct);

        var render = target.GetComponent<Render>();
        var center = render?.InteractCenterNum ?? target.PosNum;

        var rect = _gameState.WindowRect;
        var screenCoord = _gameState.WorldToScreen(center);

        // Never click an interactable whose screen point is outside the window —
        // clamping would hit something else entirely. Step closer and re-project.
        if (!CoordinateHelpers.IsInsideWindow(screenCoord, rect.Width, rect.Height,
                CoordinateHelpers.ClickMarginX, CoordinateHelpers.ClickMarginY))
        {
            await MoveToTarget(target.GridPosNum, ct, InteractDistance * 0.8f);
            screenCoord = _gameState.WorldToScreen(center);
            if (!CoordinateHelpers.IsInsideWindow(screenCoord, rect.Width, rect.Height,
                    CoordinateHelpers.ClickMarginX, CoordinateHelpers.ClickMarginY))
            {
                _log($"Entity stays outside the window, not clicking ({target.Path}).");
                return false;
            }
        }

        // Hover attempts: exact interact center first (with one fresh
        // re-projection — the camera can settle after the standstill wait),
        // then small jittered offsets around it. Something else can sit exactly
        // on the center and steal the highlight there (a minion, another
        // entity's label, a doodad) while a nearby point of the model is free.
        var targeted = false;
        for (var attempt = 0; attempt < 4 && !targeted && !ct.IsCancellationRequested; attempt++)
        {
            screenCoord = _gameState.WorldToScreen(center);
            var hoverPos = new Vector2(screenCoord.X + rect.Left, screenCoord.Y + rect.Top);
            if (attempt >= 2)
                hoverPos += new Vector2(
                    (float)(SidestepRandom.NextDouble() * 2 - 1) * 20f,
                    (float)(SidestepRandom.NextDouble() * 2 - 1) * 20f);

            await _input.MoveMouse(hoverPos, ct);
            targeted = await WaitHelpers.For(
                () => target.GetComponent<Targetable>()?.isTargeted == true,
                attempt == 0 ? 800 : 400, ct);
        }

        if (!targeted)
        {
            // A blind click would hit whatever is actually under the cursor —
            // skip it so the caller's verify+retry re-evaluates with fresh
            // state. Log what IS under the cursor: when this fails repeatedly,
            // the thief's identity is the whole diagnosis.
            _log($"Entity never highlighted under cursor -- not clicking ({target.Path}). " +
                 _gameState.DescribeHoverTarget());
            return false;
        }

        await _input.Click(MouseButtons.Left, ct);
        return true;
    }

    /// <summary>
    /// Move the mouse to a window-relative position (e.g. WorldToScreen output),
    /// applying the window offset. Throttled and hard-clamped into the window —
    /// used for hovering monsters for ReAgent.
    /// </summary>
    public async SyncTask<bool> MoveMouseToScreen(Vector2 windowRelative, CancellationToken ct)
    {
        var rect = _gameState.WindowRect;
        var clamped = CoordinateHelpers.HardClampToWindow(
            windowRelative, rect.Width, rect.Height,
            CoordinateHelpers.ClickMarginX, CoordinateHelpers.ClickMarginY);
        return await _input.HoverMouse(
            new Vector2(clamped.X + rect.Left, clamped.Y + rect.Top), ct);
    }

    /// <summary>
    /// Frame-checked wait until the character reports standing still (Actor.isMoving).
    /// Clicks on world interactables only land where projected once the character
    /// stops gliding after the move key is released — WorldToScreen shifts every
    /// frame while moving. Returns false on timeout (caller may click anyway).
    /// </summary>
    public async SyncTask<bool> WaitForStandstill(CancellationToken ct, int timeoutMs = 2000)
    {
        var stopped = await WaitHelpers.For(() => !_gameState.IsPlayerMoving, timeoutMs, ct);
        if (!stopped)
            _log("Still moving after standstill wait -- clicking anyway.");
        return stopped;
    }

    /// <summary>
    /// Release the move key if it is currently held down.
    /// </summary>
    public async SyncTask<bool> ReleaseMoveKey()
    {
        var moveKey = _getMoveKey();
        if (InputService.IsKeyDown(moveKey))
        {
            await _input.KeyUp(moveKey);
        }
        return true;
    }

    private Vector2 ConvertGridToScreen(Vector2i gridPoint)
    {
        const float gridToWorldMultiplier = 250f / 23f;
        float worldZ = _gameState.PlayerZ;

        var heightData = _gameState.RawTerrainHeightData;
        if (heightData != null &&
            gridPoint.Y >= 0 && gridPoint.Y < heightData.Length &&
            heightData[gridPoint.Y] != null &&
            gridPoint.X >= 0 && gridPoint.X < heightData[gridPoint.Y].Length)
        {
            worldZ = heightData[gridPoint.Y][gridPoint.X];
        }

        var worldPosition = new Vector3(
            gridPoint.X * gridToWorldMultiplier,
            gridPoint.Y * gridToWorldMultiplier,
            worldZ);

        var screenCoordinate = _gameState.WorldToScreen(worldPosition);
        return new Vector2(screenCoordinate.X, screenCoordinate.Y);
    }
}
