using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NightmareAutomate.Helpers;

namespace NightmareAutomate.Services;

/// <summary>
/// Discord webhook notifications — fire-and-forget from the game thread.
///
/// Callers only enqueue (instant); a background thread-pool worker (plain TPL,
/// deliberately NOT a SyncTask) owns the HTTP posting so a slow or unreachable
/// Discord can never stall the game loop. The worker batches queued messages
/// into one post (Discord allows 10 embeds per message), spaces posts to stay
/// far under the webhook rate limit, honors 429 Retry-After, and pauses itself
/// for 10 minutes after repeated hard failures. Notifications are strictly
/// best-effort — failures never affect the bot.
/// </summary>
public sealed class DiscordWebhookService : IDisposable
{
    private const int MaxEmbedsPerPost = 10;
    private const int PostSpacingMs = 2000;
    private const int MaxConsecutiveFailures = 3;

    private const int ColorCritical = 0xE74C3C; // red
    private const int ColorSummary = 0x2ECC71;  // green
    private const int ColorTest = 0x5865F2;     // blurple

    private sealed record QueuedEmbed(string Title, string Description, int Color, bool Mention, DateTime At);

    private readonly NightmareAutomateSettings _settings;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<QueuedEmbed> _queue = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly CancellationTokenSource _cts = new();
    private readonly object _workerLock = new();
    private Task? _worker;
    private int _consecutiveFailures;
    private long _disabledUntil; // 0 = not paused (monotonic deadline)

    public DiscordWebhookService(NightmareAutomateSettings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    public void NotifyCritical(string title, string message)
    {
        if (!_settings.NotifyCritical) return;
        Enqueue(title, message, ColorCritical, mention: true);
    }

    public void NotifySummary(string title, string message)
    {
        if (!_settings.NotifyRunSummary) return;
        Enqueue(title, message, ColorSummary, mention: false);
    }

    /// <summary>
    /// Settings-UI test button. Clears any failure pause so a corrected URL can
    /// be re-tested immediately.
    /// </summary>
    public void SendTest()
    {
        if (string.IsNullOrWhiteSpace(_settings.DiscordWebhookUrl))
        {
            _log("[Discord] No webhook URL configured.");
            return;
        }

        _disabledUntil = 0;
        _consecutiveFailures = 0;
        _log("[Discord] Sending a test message...");
        Enqueue("Test notification", "Nightmare Automate can reach this channel.", ColorTest, mention: false);
    }

    private void Enqueue(string title, string description, int color, bool mention)
    {
        if (string.IsNullOrWhiteSpace(_settings.DiscordWebhookUrl)) return;
        if (!Clock.Reached(_disabledUntil)) return;

        _queue.Enqueue(new QueuedEmbed(title, description, color, mention, DateTime.Now));
        EnsureWorker();
    }

    /// <summary>
    /// The worker is spawned lazily and exits after 30s idle — re-spawned on the
    /// next enqueue, so no thread lingers while notifications are off.
    /// </summary>
    private void EnsureWorker()
    {
        lock (_workerLock)
        {
            if (_worker is { IsCompleted: false }) return;
            _worker = Task.Run(WorkerLoop);
        }
    }

    private async Task WorkerLoop()
    {
        try
        {
            var idleSince = Clock.NowMs;
            while (!_cts.IsCancellationRequested)
            {
                var batch = new List<QueuedEmbed>();
                while (batch.Count < MaxEmbedsPerPost && _queue.TryDequeue(out var embed))
                    batch.Add(embed);

                if (batch.Count == 0)
                {
                    if (Clock.SecondsSince(idleSince) > 30) return;
                    await Task.Delay(250, _cts.Token);
                    continue;
                }

                idleSince = Clock.NowMs;
                await PostBatch(batch);
                await Task.Delay(PostSpacingMs, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Plugin shutdown.
        }
    }

    private async Task PostBatch(List<QueuedEmbed> batch)
    {
        var url = _settings.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        var mention = _settings.DiscordMentionOnCritical;
        var payload = new
        {
            content = batch.Any(b => b.Mention) && !string.IsNullOrWhiteSpace(mention) ? mention : null,
            embeds = batch.Select(b => new
            {
                title = b.Title,
                description = b.Description,
                color = b.Color,
                timestamp = b.At.ToUniversalTime().ToString("o")
            }).ToArray()
        };

        var json = JsonConvert.SerializeObject(payload,
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await _http.PostAsync(url,
                    new StringContent(json, Encoding.UTF8, "application/json"), _cts.Token);

                if ((int)response.StatusCode == 429)
                {
                    // Rate limited — wait what Discord asks, then retry once.
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                    await Task.Delay(retryAfter, _cts.Token);
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    _consecutiveFailures = 0;
                    return;
                }

                _log($"[Discord] Webhook returned HTTP {(int)response.StatusCode}.");
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[Discord] Webhook post failed: {ex.Message}");
                break;
            }
        }

        if (++_consecutiveFailures >= MaxConsecutiveFailures)
        {
            _consecutiveFailures = 0;
            _disabledUntil = Clock.InMinutes(10);
            _queue.Clear();
            _log("[Discord] Notifications paused for 10 minutes after repeated failures -- check the webhook URL.");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}
