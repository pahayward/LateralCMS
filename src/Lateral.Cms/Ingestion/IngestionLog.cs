using Lateral.Cms.Domain;

namespace Lateral.Cms.Ingestion;

/// <summary>
/// Source-generated log messages. Payloads and credentials are never logged: only ids, types, versions,
/// outcomes and checksums. The batch id travels with every line as the correlation id.
/// </summary>
public static partial class IngestionLog
{
    /// <summary>One line per event, written once its outcome is final and durable.</summary>
    [LoggerMessage(EventId = 1001,
        Message = "Batch {BatchId} event {Index} {EventType} entity {EntityId} version {Version}: {Outcome} {Reason} checksum {Checksum}")]
    public static partial void EventDecided(
        ILogger logger, LogLevel level, Guid batchId, int index, string? eventType, string? entityId, long? version,
        EventOutcome outcome, string? reason, string? checksum);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "Batch {BatchId} refused: {Code}")]
    public static partial void BatchRefused(ILogger logger, Guid batchId, string code);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information,
        Message = "Batch {BatchId} accepted in {ElapsedMs} ms: {Queued} queued, {Ignored} ignored, {Rejected} rejected")]
    public static partial void BatchAccepted(
        ILogger logger, Guid batchId, double elapsedMs, int queued, int ignored, int rejected);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Warning,
        Message = "Batch {BatchId} refused: the queue holds {UsedBytes} of {MaxBytes} bytes, {FailedBytes} of them in set-aside batches. Nothing was stored; the sender should retry")]
    public static partial void QueueFull(ILogger logger, Guid batchId, long usedBytes, long maxBytes, long failedBytes);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Error,
        Message = "Batch {BatchId} could not be stored; nothing was kept and the sender should retry")]
    public static partial void AcceptFailed(ILogger logger, Exception exception, Guid batchId);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information,
        Message = "Batch {BatchId} applied after {LagMs} ms in the queue (attempt {Attempt}): {Applied} applied, {Ignored} ignored, {Rejected} rejected")]
    public static partial void BatchApplied(
        ILogger logger, Guid batchId, double lagMs, int attempt, int applied, int ignored, int rejected);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning,
        Message = "Batch {BatchId} attempt {Attempt} failed and was rolled back; nothing from it was applied. Next attempt after {NextAttemptAt:O}")]
    public static partial void BatchAttemptFailed(
        ILogger logger, Exception exception, Guid batchId, int attempt, DateTime nextAttemptAt);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Error,
        Message = "Batch {BatchId} failed {Attempts} times and was set aside with its {EventCount} events. It no longer holds up other batches; an admin can requeue it")]
    public static partial void BatchSetAside(ILogger logger, Exception exception, Guid batchId, int attempts, int eventCount);

    [LoggerMessage(EventId = 1017, Level = LogLevel.Error,
        Message = "Batch {BatchId} holds an unPublish or delete and failed {Attempts} times. Its entities stay visible until it succeeds; retrying after {NextAttemptAt:O}")]
    public static partial void TakedownBatchStillFailing(
        ILogger logger, Exception exception, Guid batchId, int attempts, DateTime nextAttemptAt);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information, Message = "The batch worker started")]
    public static partial void WorkerStarted(ILogger logger);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Information, Message = "The batch worker stopped")]
    public static partial void WorkerStopped(ILogger logger);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Warning,
        Message = "The batch worker is switched off (Inbox:WorkerEnabled=false). Accepted batches will wait until it runs")]
    public static partial void WorkerDisabled(ILogger logger);

    [LoggerMessage(EventId = 1016, Level = LogLevel.Error,
        Message = "The batch worker hit an unexpected error and will try again shortly")]
    public static partial void WorkerFault(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
        Message = "Admin {Admin} set disabled={Disabled} on entity {EntityId} (changed: {Changed})")]
    public static partial void AdminOverride(ILogger logger, string admin, bool disabled, string entityId, bool changed);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Admin {Admin} RESET all data: {Entities} entities, {Tombstones} tombstones, {LedgerRows} ledger rows and {Batches} batches erased (the audit log is kept)")]
    public static partial void AdminReset(ILogger logger, string admin, int entities, int tombstones, int ledgerRows, int batches);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning,
        Message = "POST /api/admin/reset is ENABLED (Admin:AllowReset). Any admin can erase all data. For testing only")]
    public static partial void ResetEnabled(ILogger logger);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Information, Message = "Admin {Admin} requeued failed batch {BatchId}")]
    public static partial void AdminRequeue(ILogger logger, string admin, Guid batchId);
}
