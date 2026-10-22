namespace AdvancedRag.Core.Ai;

/// <summary>
/// Sliding one-minute window of how many texts were sent per key (a model name). Gemini counts every text in a batch,
/// and the free-tier quota is per project and model, so ingestion and search must share one budget per model.
/// </summary>
public sealed class EmbeddingRateLimiter(ILogger<EmbeddingRateLimiter> logger)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, Queue<(DateTimeOffset SentAt, int Count)>> _sent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Waits until <paramref name="count"/> more texts fit in the budget, then records them as sent.</summary>
    public async Task WaitAsync(string key, int count, int perMinute, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sent.TryGetValue(key, out var history)) _sent[key] = history = new();

            var wait = ComputeWait(history, DateTimeOffset.UtcNow, count, perMinute);
            if (wait > TimeSpan.Zero)
            {
                logger.LogInformation("Rate limiter ({Key}): waiting {Seconds:0.0}s to stay under {Limit} texts/minute", key, wait.TotalSeconds, perMinute);
                await Task.Delay(wait, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            while (history.Count > 0 && now - history.Peek().SentAt >= Window) history.Dequeue();
            history.Enqueue((now, count));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How long to wait before sending <paramref name="count"/> more texts without exceeding <paramref name="perMinute"/>.</summary>
    public static TimeSpan ComputeWait(IEnumerable<(DateTimeOffset SentAt, int Count)> history, DateTimeOffset now, int count, int perMinute)
    {
        var recent = history.Where(h => now - h.SentAt < Window).OrderBy(h => h.SentAt).ToList();
        var used = recent.Sum(h => h.Count);
        if (used + count <= perMinute) return TimeSpan.Zero;

        foreach (var entry in recent)
        {
            used -= entry.Count;
            if (used + count <= perMinute) return entry.SentAt + Window - now;
        }
        return Window;
    }
}
