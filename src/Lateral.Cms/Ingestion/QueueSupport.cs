using Lateral.Cms.Domain;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Ingestion;

/// <summary>The queue has no room for a batch. Nothing was stored, so the sender can simply try again later.</summary>
public sealed class InboxFullException(long usedBytes, long maxBytes)
    : Exception($"The queue holds {usedBytes} of {maxBytes} bytes and cannot take this batch.")
{
    public long UsedBytes { get; } = usedBytes;

    public long MaxBytes { get; } = maxBytes;
}

/// <summary>Wakes the worker the moment a batch is accepted, so the queue adds no delay when it is empty.</summary>
public sealed class WakeSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Notify()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; one wake-up covers every batch waiting.
        }
    }

    /// <summary>Waits for a signal or for the timeout, whichever is first. Throws if cancelled.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _wake.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// The latest backlog figures, written by the worker and read by the metrics gauges, so a gauge never queries the
/// database itself.
/// </summary>
public sealed class QueueBacklog
{
    private long _pending;
    private long _failed;
    private double _oldestAgeSeconds;

    public long PendingBatches => Interlocked.Read(ref _pending);

    public long FailedBatches => Interlocked.Read(ref _failed);

    public double OldestPendingAgeSeconds => Volatile.Read(ref _oldestAgeSeconds);

    public void Set(QueueStats stats, DateTime now)
    {
        Interlocked.Exchange(ref _pending, stats.PendingBatches);
        Interlocked.Exchange(ref _failed, stats.FailedBatches);
        Volatile.Write(ref _oldestAgeSeconds, stats.OldestPending is { } oldest ? Math.Max(0, (now - oldest).TotalSeconds) : 0);
    }
}

/// <summary>A snapshot of what the queue holds.</summary>
public sealed record QueueStats(
    int PendingBatches, int PendingEvents, DateTime? OldestPending, int FailedBatches, long UsedBytes)
{
    /// <summary>
    /// Reads the figures with one grouped query that only touches batches still queued or failed (an index range
    /// each), however many completed batches the table holds.
    /// </summary>
    public static async Task<QueueStats> ReadAsync(IQueryable<BatchRow> batches, CancellationToken cancellationToken)
    {
        var rows = await batches
            .Where(b => b.Status == BatchState.Queued || b.Status == BatchState.Failed)
            .GroupBy(b => b.Status)
            .Select(g => new
            {
                Status = g.Key,
                Batches = g.Count(),
                Events = g.Sum(b => b.EventCount),
                Bytes = g.Sum(b => b.PayloadBytes),
                Oldest = g.Min(b => b.ReceivedAt),
            })
            .ToListAsync(cancellationToken);

        var pending = rows.FirstOrDefault(r => r.Status == BatchState.Queued);
        var failed = rows.FirstOrDefault(r => r.Status == BatchState.Failed);
        return new QueueStats(
            pending?.Batches ?? 0, pending?.Events ?? 0, pending?.Oldest, failed?.Batches ?? 0,
            (pending?.Bytes ?? 0) + (failed?.Bytes ?? 0));
    }
}

/// <summary>A queue that could never hold a single full-size request would make every large batch fail forever.</summary>
public sealed class InboxOptionsValidator(IOptions<IngestionOptions> ingestion) : IValidateOptions<InboxOptions>
{
    public ValidateOptionsResult Validate(string? name, InboxOptions options) =>
        options.MaxBytes < ingestion.Value.MaxBodyBytes
            ? ValidateOptionsResult.Fail(
                $"Inbox:MaxBytes ({options.MaxBytes}) must be at least Ingestion:MaxBodyBytes ({ingestion.Value.MaxBodyBytes}), " +
                "or a batch of the largest accepted size could never be stored.")
            : options.RetryMaxDelay < options.RetryBaseDelay
                ? ValidateOptionsResult.Fail("Inbox:RetryMaxDelay must not be shorter than Inbox:RetryBaseDelay.")
                : ValidateOptionsResult.Success;
}
