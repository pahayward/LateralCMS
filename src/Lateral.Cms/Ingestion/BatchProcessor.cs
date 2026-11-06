using Lateral.Cms.Domain;
using Lateral.Cms.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Ingestion;

/// <summary>What one call to <see cref="BatchProcessor.ProcessNextAsync"/> did.</summary>
public enum ProcessStep
{
    /// <summary>Nothing was due.</summary>
    Idle,

    /// <summary>A batch was applied and marked completed.</summary>
    Applied,

    /// <summary>A batch failed and will be tried again after a delay.</summary>
    Retrying,

    /// <summary>A batch failed for the last time and was set aside.</summary>
    SetAside,
}

/// <summary>
/// The applying half of the pipeline: takes the next batch from the queue, reconciles its events with the stored
/// state and records the outcomes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One effect per batch.</b> Entity and tombstone changes, every event's outcome (with its payload removed) and the
/// batch's completion are a single transaction. A crash, a cancellation or an error rolls all of it back and the batch
/// is exactly as it was, to be picked up again. Nothing is half-applied and nothing is lost, which is also why shutting
/// down needs no draining.
/// </para>
/// <para>
/// <b>Order.</b> Batches holding a takedown go first, then oldest first. Reordering is safe only because reconciliation
/// does not depend on arrival order (under the ordering assumption in the design, which the permutation tests prove).
/// </para>
/// <para>
/// <b>A bad batch cannot block the rest.</b> A failure is counted and retried after a growing delay; after
/// <see cref="InboxOptions.MaxAttempts"/> a batch is set aside as failed and the next one is picked. A batch that holds a
/// takedown is never set aside: leaving it would keep a confidential entity visible, so it keeps retrying at the longest
/// delay until it succeeds, loudly.
/// </para>
/// </remarks>
public sealed class BatchProcessor(
    CmsWriteDbContext db,
    WriteGate gate,
    QueueBacklog backlog,
    TimeProvider time,
    IOptions<InboxOptions> options,
    CmsMetrics metrics,
    ILogger<BatchProcessor> logger)
{
    /// <summary>
    /// The batches that may be picked now, best first: takedowns, then oldest. Public so a test can check that the SQL it
    /// becomes is served by <c>IX_Batches_Pick</c> with no sort.
    /// </summary>
    public static IQueryable<BatchRow> NextDue(IQueryable<BatchRow> batches, DateTime now) =>
        batches
            .Where(b => b.Status == BatchState.Queued && b.NextAttemptAt <= now)
            .OrderByDescending(b => b.HasTakedown)
            .ThenBy(b => b.Id);

    /// <summary>Applies the next due batch, if there is one.</summary>
    public async Task<ProcessStep> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        // One batch per turn at the gate, so a long backlog never keeps new batches from being accepted.
        using var _ = await gate.AcquireAsync(cancellationToken);
        db.ChangeTracker.Clear();
        var now = time.GetUtcNow().UtcDateTime;

        var batch = await NextDue(db.Batches, now).FirstOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            return ProcessStep.Idle;
        }

        var batchId = batch.BatchId;
        var receivedAt = batch.ReceivedAt;
        var eventCount = batch.EventCount;
        var takedown = batch.HasTakedown;
        var attempt = batch.Attempts + 1;
        var started = time.GetTimestamp();

        try
        {
            var queued = await db.Events
                .Where(e => e.BatchId == batchId && e.Outcome == EventOutcome.Queued)
                .OrderBy(e => e.Sequence)
                .ToListAsync(cancellationToken);

            await ReduceAsync(queued, now, cancellationToken);
            batch.Status = BatchState.Completed;
            batch.CompletedAt = now;
            batch.LastError = null;
            await db.SaveChangesAsync(cancellationToken);

            // Durable now: report.
            Report(batchId, queued);
            var applied = queued.Count(e => e.Outcome == EventOutcome.Applied);
            var lagMs = Math.Max(0, (now - receivedAt).TotalMilliseconds);
            metrics.BatchCompleted(time.GetElapsedTime(started).TotalMilliseconds);
            metrics.QueueLag(lagMs);
            IngestionLog.BatchApplied(logger, batchId, lagMs, attempt, applied, queued.Count - applied, 0);
            return ProcessStep.Applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return await RecordFailureAsync(batchId, eventCount, takedown, attempt, now, ex, settings, cancellationToken);
        }
    }

    /// <summary>Refreshes the figures behind the backlog gauges.</summary>
    public async Task RefreshBacklogAsync(CancellationToken cancellationToken)
    {
        var stats = await QueueStats.ReadAsync(db.Batches.AsNoTracking(), cancellationToken);
        backlog.Set(stats, time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Folds the queued events per entity id with the pure reducer, then stages the difference between each id's state
    /// before and after as inserts, updates and deletes. Two queries however many events there are.
    /// </summary>
    private async Task ReduceAsync(List<EventRow> queued, DateTime now, CancellationToken cancellationToken)
    {
        if (queued.Count == 0)
        {
            return;
        }

        var ids = queued.Select(e => e.EntityId!).Distinct(StringComparer.Ordinal).ToList();
        var entities = await db.Entities
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, StringComparer.Ordinal, cancellationToken);
        var tombstones = await db.Tombstones
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, StringComparer.Ordinal, cancellationToken);

        // GroupBy keeps the order inside each group, so repeated ids in one batch fold in sequence.
        foreach (var group in queued.GroupBy(e => e.EntityId!, StringComparer.Ordinal))
        {
            entities.TryGetValue(group.Key, out var entityRow);
            tombstones.TryGetValue(group.Key, out var tombstoneRow);

            var entityBefore = entityRow?.ToState();
            var tombstoneBefore = tombstoneRow?.ToState();
            var entity = entityBefore;
            var tombstone = tombstoneBefore;

            foreach (var row in group)
            {
                var reduction = Reducer.Apply(
                    entity, tombstone,
                    new CmsEvent(row.EntityId!, row.Type!.Value, row.Version, row.Timestamp!.Value, row.Payload, row.PayloadChecksum),
                    now);
                entity = reduction.Entity;
                tombstone = reduction.Tombstone;

                row.Outcome = reduction.Outcome;
                row.Reason = reduction.Reason;
                row.ProcessedAt = now;
                row.Payload = null;
            }

            // The persistence layer turns before and after into the right statement. A delete followed by a re-create
            // in the same fold is simply an update of the same row.
            if (entityRow is null && entity is not null)
            {
                db.Entities.Add(entity.ToRow());
            }
            else if (entityRow is not null && entity is null)
            {
                db.Entities.Remove(entityRow);
            }
            else if (entityRow is not null && entity != entityBefore)
            {
                entity!.CopyTo(entityRow);
            }

            if (tombstoneRow is null && tombstone is not null)
            {
                db.Tombstones.Add(new TombstoneRow { Id = tombstone.Id, DeletedAt = tombstone.DeletedAt, RecordedAt = tombstone.RecordedAt });
            }
            else if (tombstoneRow is not null && tombstone != tombstoneBefore)
            {
                tombstoneRow.DeletedAt = tombstone!.DeletedAt;
                tombstoneRow.RecordedAt = tombstone.RecordedAt;
            }
        }
    }

    /// <summary>Logs and counts the decided outcomes. Call it only once they are durable.</summary>
    private void Report(Guid batchId, List<EventRow> decided)
    {
        foreach (var row in decided)
        {
            // The winner of a content conflict is "applied", but its loser logs at Warning, so the winner does too, or
            // the level would depend on which of the two arrived first.
            var level = row.Outcome is EventOutcome.Applied or EventOutcome.IgnoredDuplicate
                && row.Reason != ReasonCodes.SameVersionDifferentContent
                ? LogLevel.Information
                : LogLevel.Warning;
            IngestionLog.EventDecided(
                logger, level, batchId, row.Sequence, row.RawType, row.EntityId, row.Version, row.Outcome, row.Reason,
                row.PayloadChecksum);
            metrics.EventProcessed(row.Outcome, row.Type);
        }
    }

    private async Task<ProcessStep> RecordFailureAsync(
        Guid batchId, int eventCount, bool takedown, int attempt, DateTime now, Exception failure, InboxOptions settings,
        CancellationToken cancellationToken)
    {
        var finalAttempt = !takedown && attempt >= settings.MaxAttempts;
        var next = finalAttempt ? now : now + settings.DelayBefore(attempt + 1);

        // Only the kind of failure is kept. A message could carry data, and the full exception is in the log.
        var sqlite = failure as SqliteException ?? failure.InnerException as SqliteException;
        var kind = sqlite is null
            ? failure.GetType().Name
            : $"{failure.GetType().Name}/SqliteException({sqlite.SqliteErrorCode})";

        // Recorded on its own, outside the rolled-back transaction. If even this fails the exception goes to the
        // worker, which backs off; the batch is unchanged and will simply be tried again.
        await db.Batches
            .Where(b => b.BatchId == batchId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(b => b.Attempts, attempt)
                    .SetProperty(b => b.Status, finalAttempt ? BatchState.Failed : BatchState.Queued)
                    .SetProperty(b => b.NextAttemptAt, next)
                    .SetProperty(b => b.LastError, kind),
                cancellationToken);

        if (finalAttempt)
        {
            metrics.BatchSetAside();
            IngestionLog.BatchSetAside(logger, failure, batchId, attempt, eventCount);
            return ProcessStep.SetAside;
        }

        metrics.BatchRetried();
        if (takedown)
        {
            IngestionLog.TakedownBatchStillFailing(logger, failure, batchId, attempt, next);
        }
        else
        {
            IngestionLog.BatchAttemptFailed(logger, failure, batchId, attempt, next);
        }

        return ProcessStep.Retrying;
    }
}
