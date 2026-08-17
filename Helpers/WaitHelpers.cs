using System;
using System.Threading;
using ExileCore.Shared;

namespace NightmareAutomate.Helpers;

/// <summary>
/// The one way to wait for a game-state condition: frame-checked with a hard
/// timeout. External cancellation rethrows (OperationCanceledException); the
/// timeout merely returns false — callers decide whether to retry, skip, or
/// proceed anyway.
/// </summary>
public static class WaitHelpers
{
    public static async SyncTask<bool> For(Func<bool> condition, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var ok = await TaskUtils.CheckEveryFrame(condition, cts.Token);
        ct.ThrowIfCancellationRequested();
        return ok;
    }

    /// <summary>
    /// Wrap a condition so it is re-evaluated at most every intervalMs, returning
    /// the last result in between. For stopWhen/abort lambdas that scan the entity
    /// list or UI labels -- those run every frame during a walk and are not free.
    /// </summary>
    public static Func<bool> Throttle(Func<bool> condition, int intervalMs)
    {
        var lastCheck = Clock.Never;
        var lastResult = false;
        return () =>
        {
            if (Clock.MsSince(lastCheck) < intervalMs)
                return lastResult;
            lastCheck = Clock.NowMs;
            lastResult = condition();
            return lastResult;
        };
    }
}
