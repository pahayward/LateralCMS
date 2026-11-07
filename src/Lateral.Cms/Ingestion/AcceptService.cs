using System.Text.Json;
using Lateral.Cms.Domain;
using Lateral.Cms.Integrity;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Ingestion;

/// <summary>
/// The accepting half of the pipeline. It does everything that needs no stored state (validate, canonicalise,
/// checksum), then stores the batch in <b>one transaction</b> and returns. The sender is told <c>202</c> only after
/// that commit, so an acknowledged event survives a crash. <see cref="BatchProcessor"/> applies it later.
/// </summary>
/// <remarks>
/// Events that are already decided (rejected, or of a type this service ignores) get their ledger row in the same
/// transaction, with no payload; only events that still need reconciling are queued. Accepting reads no entity state.
/// </remarks>
public sealed class AcceptService(
    CmsWriteDbContext db,
    WriteGate gate,
    WakeSignal signal,
    TimeProvider time,
    IOptions<IngestionOptions> options,
    IOptions<InboxOptions> inboxOptions,
    IPayloadChecksum checksums,
    CmsMetrics metrics,
    ILogger<AcceptService> logger)
{
    /// <exception cref="InboxFullException">The queue has no room; nothing was stored.</exception>
    public async Task<IngestResult> AcceptAsync(Guid batchId, JsonElement batch, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var started = time.GetTimestamp();
        var now = time.GetUtcNow().UtcDateTime;

        // 1. Validate every event on its own. A bad event never blocks its neighbours. No database access.
        var parsed = new List<ParsedEvent>();
        foreach (var element in batch.EnumerateArray())
        {
            parsed.Add(EventParser.Parse(element, parsed.Count, now, settings, checksums));
        }

        // 2. Build the rows. Queued events keep their payload; decided ones are ledger rows from the start.
        var queued = parsed.Where(p => p.Event is not null).ToList();
        var row = new BatchRow
        {
            BatchId = batchId,
            ReceivedAt = now,
            EventCount = queued.Count,
            // Canonical payloads are pure ASCII (everything else is \u-escaped), so characters are bytes.
            PayloadBytes = queued.Sum(p => (long)(p.Event!.Payload?.Length ?? 0)),
            HasTakedown = queued.Any(p => p.Event!.Type != CmsEventType.Publish),
            Status = queued.Count > 0 ? BatchState.Queued : BatchState.Completed,
            NextAttemptAt = now,
            CompletedAt = queued.Count > 0 ? null : now,
            Events = [.. parsed.Select(p => ToRow(batchId, p, now))],
        };

        // 3. Store, under the write gate so two senders cannot both squeeze in past the size cap.
        using (await gate.AcquireAsync(cancellationToken))
        {
            db.ChangeTracker.Clear();
            if (queued.Count > 0)
            {
                var limit = inboxOptions.Value.MaxBytes;
                var used = await db.Batches
                    .Where(b => b.Status == BatchState.Queued || b.Status == BatchState.Failed)
                    .SumAsync(b => b.PayloadBytes, cancellationToken);
                if (used + row.PayloadBytes > limit)
                {
                    var failedBytes = await db.Batches.Where(b => b.Status == BatchState.Failed).SumAsync(b => b.PayloadBytes, cancellationToken);
                    IngestionLog.QueueFull(logger, batchId, used, limit, failedBytes);
                    throw new InboxFullException(used, limit);
                }
            }

            db.Batches.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        // 4. Only now that it is durable: wake the worker and tell the sender.
        if (queued.Count > 0)
        {
            signal.Notify();
        }

        var results = new EventResult[parsed.Count];
        foreach (var item in parsed)
        {
            if (item.Event is null)
            {
                results[item.Index] = new EventResult(
                    item.Index, item.EntityId, item.EventType, item.Version, item.EarlyOutcome!.Value, item.Reason, null);
                ReportDecided(batchId, results[item.Index], item.EarlyOutcome.Value);
            }
            else
            {
                results[item.Index] = new EventResult(
                    item.Index, item.EntityId, item.EventType, item.Version, EventOutcome.Queued, null, item.PayloadChecksum);
                metrics.EventQueued(item.Event.Type);
            }
        }

        var elapsedMs = time.GetElapsedTime(started).TotalMilliseconds;
        metrics.BatchAccepted(elapsedMs);
        var summary = IngestResult.From(batchId, row.Status, results, receivedAt: now, completedAt: row.CompletedAt);
        IngestionLog.BatchAccepted(logger, batchId, elapsedMs, summary.Queued, summary.Ignored, summary.Rejected);
        return summary;
    }

    private static EventRow ToRow(Guid batchId, ParsedEvent p, DateTime now) =>
        new()
        {
            BatchId = batchId,
            Sequence = p.Index,
            EntityId = p.EntityId,
            RawType = p.EventType ?? "unknown",
            Type = p.Event?.Type,
            Version = p.Version,
            Timestamp = p.Timestamp,
            Payload = p.Event?.Payload,
            PayloadChecksum = p.PayloadChecksum,
            Outcome = p.Event is null ? p.EarlyOutcome!.Value : EventOutcome.Queued,
            Reason = p.Reason,
            ReceivedAt = now,
            ProcessedAt = p.Event is null ? now : null,
        };

    private void ReportDecided(Guid batchId, EventResult result, EventOutcome outcome)
    {
        IngestionLog.EventDecided(
            logger, LogLevel.Warning, batchId, result.Index, result.Type, result.Id, result.Version, outcome, result.Reason,
            result.Checksum);
        metrics.EventProcessed(outcome, null);
    }
}
