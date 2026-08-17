using System;

namespace NightmareAutomate.Helpers;

/// <summary>
/// Monotonic time source for every duration, backoff, throttle, watchdog and
/// deadline. Wall-clock DateTime.Now is not monotonic — a DST shift or NTP
/// correction mid-session moves it by minutes-to-hours, which would instantly
/// expire (or extend) every timer of an overnight run. DateTime remains only
/// for display and webhook timestamps.
/// </summary>
public static class Clock
{
    /// <summary>Sentinel for "never happened": SecondsSince/MsSince report an
    /// eternity, matching the old DateTime.MinValue convention. Deadline fields
    /// ("...Until") should instead default to 0, which always reads as reached.</summary>
    public const long Never = long.MinValue;

    /// <summary>Milliseconds since system boot — never jumps.</summary>
    public static long NowMs => Environment.TickCount64;

    public static double MsSince(long thenMs) =>
        thenMs == Never ? double.MaxValue : NowMs - thenMs;

    public static double SecondsSince(long thenMs) =>
        thenMs == Never ? double.MaxValue : (NowMs - thenMs) / 1000.0;

    /// <summary>A deadline the given duration from now (check with <see cref="Reached"/>).</summary>
    public static long InMs(double ms) => NowMs + (long)ms;

    public static long InSeconds(double seconds) => NowMs + (long)(seconds * 1000);

    public static long InMinutes(double minutes) => NowMs + (long)(minutes * 60_000);

    public static bool Reached(long deadlineMs) => NowMs >= deadlineMs;
}
