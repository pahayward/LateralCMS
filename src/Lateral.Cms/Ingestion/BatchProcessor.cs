using Lateral.Cms.Assets;
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
    Lateral.Cms.Assets.IAssetStore store,
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

            var erased = await ReduceAsync(queued, now, cancellationToken);
            batch.Status = BatchState.Completed;
            batch.CompletedAt = now;
            batch.LastError = null;
            await db.SaveChangesAsync(cancellationToken);

            // The rows are gone for good now; take the bytes with them. A crash between the commit and here leaves files that no
            // row accounts for, which the asset sweeper wipes.
            await ErasePhysicallyAsync(erased, batchId, cancellationToken);

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
    private async Task<Erasure> ReduceAsync(List<EventRow> queued, DateTime now, CancellationToken cancellationToken)
    {
        if (queued.Count == 0)
        {
            return Erasure.None;
        }

        var deleted = new HashSet<string>(StringComparer.Ordinal);

        // Records whose stored lifetime ended in this fold. A superset of deleted: a delete followed by a re-create in the same batch
        // leaves a record, but the files only its first lifetime named are just as deletable.
        var ended = new HashSet<string>(StringComparer.Ordinal);
        var surviving = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

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
                if (entity is not null && reduction.Entity is null)
                {
                    ended.Add(group.Key);
                }

                entity = reduction.Entity;
                tombstone = reduction.Tombstone;

                row.Outcome = reduction.Outcome;
                row.Reason = reduction.Reason;
                row.ProcessedAt = now;
                row.Payload = null;
            }

            if (entity is null)
            {
                if (entityRow is not null)
                {
                    deleted.Add(group.Key);
                }

                // A deleted entity keeps nothing that came from its content, not even the hash of it.
                foreach (var row in group)
                {
                    row.PayloadChecksum = null;
                }
            }
            else
            {
                surviving[group.Key] = AssetReferences.In(entity.Payload);
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

        return await LinkAndEraseAsync(ids, deleted, ended, surviving, now, cancellationToken);
    }

    /// <summary>
    /// Keeps the record-to-file links in step with the records just reduced, and, for each record that was deleted, deletes the files
    /// only it named: their rows, here, in the same transaction; their bytes afterwards (see <see cref="ErasePhysicallyAsync"/>). A file
    /// that another record still names stays. Also forgets, for a deleted record, the hash of each earlier version of its content that
    /// the ledger kept.
    /// </summary>
    private async Task<Erasure> LinkAndEraseAsync(
        List<string> ids, HashSet<string> deleted, HashSet<string> ended, Dictionary<string, HashSet<Guid>> surviving, DateTime now, CancellationToken cancellationToken)
    {
        var links = await db.AssetLinks.Where(l => ids.Contains(l.EntityId)).ToListAsync(cancellationToken);

        // Surviving records: make their links match their payload.
        foreach (var (entityId, wanted) in surviving)
        {
            var have = links.Where(l => l.EntityId == entityId).ToList();
            foreach (var stale in have.Where(l => !wanted.Contains(l.AssetId)))
            {
                db.AssetLinks.Remove(stale);
            }

            foreach (var id in wanted.Where(w => have.All(l => l.AssetId != w)))
            {
                db.AssetLinks.Add(new AssetLinkRow { AssetId = id, EntityId = entityId });
            }
        }

        if (ended.Count == 0)
        {
            return Erasure.None;
        }

        // Records whose lifetime ended: note which files they named. A deleted record's links go; a re-created one's were already
        // made to match its new payload above, and a file it names again is kept by the check below.
        var named = new HashSet<Guid>();
        foreach (var link in links.Where(l => ended.Contains(l.EntityId)))
        {
            named.Add(link.AssetId);
            if (deleted.Contains(link.EntityId))
            {
                db.AssetLinks.Remove(link);
            }
        }

        // The ledger kept a hash of each earlier version's content; that is derived from what is being erased.
        var olderRows = await db.Events
            .Where(e => e.PayloadChecksum != null && e.Outcome != EventOutcome.Queued && deleted.Contains(e.EntityId!))
            .ToListAsync(cancellationToken);
        foreach (var row in olderRows)
        {
            row.PayloadChecksum = null;
        }

        var paths = new List<string>();
        if (named.Count > 0)
        {
            // Still named by a record this batch did not touch, or by one it just changed to name it.
            var stillNamed = await db.AssetLinks
                .Where(l => named.Contains(l.AssetId) && !ids.Contains(l.EntityId))
                .Select(l => l.AssetId)
                .ToListAsync(cancellationToken);
            var keep = stillNamed.Concat(surviving.Values.SelectMany(v => v)).ToHashSet();
            var orphaned = named.Where(a => !keep.Contains(a)).ToList();

            var assets = await db.Assets.Where(a => orphaned.Contains(a.Id)).ToListAsync(cancellationToken);
            var shas = assets.Select(a => a.Sha256).Distinct().ToList();
            var sharedWithOthers = (await db.Assets
                    .Where(a => shas.Contains(a.Sha256) && a.State != AssetState.Rejected && !orphaned.Contains(a.Id))
                    .Select(a => a.Sha256)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var asset in assets)
            {
                db.Assets.Remove(asset);
                db.Audit(now, "system", "asset.deleted", asset.Id.ToString(), $"its record was deleted; was {asset.State}");
                paths.Add(store.QuarantinePath(asset.Id));
                if (!sharedWithOthers.Contains(asset.Sha256))
                {
                    paths.Add(store.BlobPath(asset.Sha256));
                }
            }
        }

        return new Erasure(paths, ended.Count);
    }

    /// <summary>What a batch erased: the files to overwrite and remove, and how many records were deleted.</summary>
    private sealed record Erasure(List<string> Paths, int Records)
    {
        public static readonly Erasure None = new([], 0);

        public bool Any => Records > 0 || Paths.Count > 0;
    }

    /// <summary>
    /// After the commit: overwrites and removes the files, then truncates the database's write-ahead log, which still holds the
    /// earlier pages of the rows that were just deleted (the database itself overwrites freed space, see <c>secure_delete</c>).
    /// Best effort and logged: anything missed is removed by the sweeper or by the next checkpoint.
    /// </summary>
    private async Task ErasePhysicallyAsync(Erasure erased, Guid batchId, CancellationToken cancellationToken)
    {
        if (!erased.Any)
        {
            return;
        }

        var failed = 0;
        foreach (var path in erased.Paths)
        {
            if (!await store.WipeAsync(path, cancellationToken))
            {
                failed++;
            }
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "The write-ahead log could not be truncated after batch {BatchId}; it is overwritten as the database is used", batchId);
        }

        logger.LogInformation(
            "Batch {BatchId} deleted {Records} records and erased {Files} files from disk ({Failed} left for the sweeper)",
            batchId, erased.Records, erased.Paths.Count, failed);
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
