using System;

namespace NightmareAutomate.Helpers;

/// <summary>
/// Routine progress: what the bot is doing while everything goes to plan. Shown on
/// the debug overlay, and written to the log file only with "Verbose Logs" on.
/// Outcomes and problems go through the normal log instead.
/// </summary>
public static class BotLog
{
    /// <summary>Wired up by the plugin at Initialise.</summary>
    public static Action<string>? StepLog;

    public static void Step(string message) => StepLog?.Invoke(message);
}
