using System;
using System.Collections.Concurrent;

namespace NightmareAutomate.Helpers;

/// <summary>
/// The one way to wrap a game-memory read that may fail mid-update (stale
/// entity addresses, panels tearing down). A bare silent catch would also
/// swallow genuine bugs in our own logic — this counts failures per call site
/// and logs the first occurrence (then every 500th) with the exception type,
/// so a NullReference from a code bug is visible instead of vanishing
/// identically to a routine stale-memory hiccup.
/// </summary>
public static class SafeRead
{
    /// <summary>Wired to the file log by the plugin at Initialise. Thread-safe
    /// callers only enqueue strings.</summary>
    public static Action<string>? Log;

    private static readonly ConcurrentDictionary<string, int> Counts = new();

    public static T? Try<T>(string site, Func<T> read, T? fallback = default)
    {
        try
        {
            return read();
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is control flow, never a failed read
        }
        catch (Exception ex)
        {
            Note(site, ex);
            return fallback;
        }
    }

    /// <summary>For catch blocks that need custom recovery logic but should
    /// still be counted/surfaced.</summary>
    public static void Note(string site, Exception ex)
    {
        var n = Counts.AddOrUpdate(site, 1, (_, c) => c + 1);
        if (n == 1 || n % 500 == 0)
            Log?.Invoke($"[SafeRead] '{site}' failed ({n}x): {ex.GetType().Name}: {ex.Message}");
    }
}
