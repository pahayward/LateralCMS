using Lateral.Cms.Domain;

namespace Lateral.Cms.Persistence;

/// <summary>Stored form of <see cref="EntitySnapshot"/>, plus the optimistic concurrency token.</summary>
public sealed class EntityRow
{
    public string Id { get; set; } = "";

    public long Version { get; set; }

    public string Payload { get; set; } = "";

    public string PayloadChecksum { get; set; } = "";

    public bool IsPublished { get; set; }

    public bool IsDisabledByAdmin { get; set; }

    public string? DisabledBy { get; set; }

    public DateTime? DisabledAt { get; set; }

    public DateTime LastEventTimestamp { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Replaced by the write context on every save, so a save based on a stale read is refused.</summary>
    public Guid ConcurrencyStamp { get; set; }
}

/// <summary>Stored form of <see cref="TombstoneState"/>. Holds no entity data.</summary>
public sealed class TombstoneRow
{
    public string Id { get; set; } = "";

    public DateTime DeletedAt { get; set; }

    public DateTime RecordedAt { get; set; }
}

/// <summary>One accepted request: the queue entry and, once completed, the record that it was applied.</summary>
public sealed class BatchRow
{
    /// <summary>First-in, first-out order within a priority.</summary>
    public long Id { get; set; }

    /// <summary>The correlation id returned to the sender.</summary>
    public Guid BatchId { get; set; }

    public DateTime ReceivedAt { get; set; }

    /// <summary>Events that were queued for applying when the batch was accepted.</summary>
    public int EventCount { get; set; }

    /// <summary>Size of the queued payloads, for the queue's size cap.</summary>
    public long PayloadBytes { get; set; }

    /// <summary>Holds an unPublish or delete. Such batches are applied first: a takedown must not wait behind a backlog.</summary>
    public bool HasTakedown { get; set; }

    public BatchState Status { get; set; }

    public int Attempts { get; set; }

    /// <summary>Not picked up before this time (retry back-off).</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>Kind of the last failure: an exception type and database code, never a message, which could carry data.</summary>
    public string? LastError { get; set; }

    public DateTime? CompletedAt { get; set; }

    public List<EventRow> Events { get; set; } = [];
}

/// <summary>
/// One row per event received, whatever its outcome: the queue while it waits and the ledger once it is decided.
/// A payload exists only while the event is queued; the schema makes any other state impossible.
/// </summary>
public sealed class EventRow
{
    public long Id { get; set; }

    public Guid BatchId { get; set; }

    /// <summary>Position within the sender's batch.</summary>
    public int Sequence { get; set; }

    /// <summary>Null when the event was too malformed to yield a usable id.</summary>
    public string? EntityId { get; set; }

    /// <summary>Event type as sent, with control characters masked and the length bounded.</summary>
    public string RawType { get; set; } = "";

    /// <summary>Null when the type is missing or not one this service understands.</summary>
    public CmsEventType? Type { get; set; }

    public long? Version { get; set; }

    public DateTime? Timestamp { get; set; }

    /// <summary>Canonical payload JSON. Non-null only while <see cref="Outcome"/> is <see cref="EventOutcome.Queued"/>.</summary>
    public string? Payload { get; set; }

    public string? PayloadChecksum { get; set; }

    public EventOutcome Outcome { get; set; }

    public string? Reason { get; set; }

    public DateTime ReceivedAt { get; set; }

    /// <summary>When the outcome was decided. Null while queued.</summary>
    public DateTime? ProcessedAt { get; set; }
}

/// <summary>Moves state between the immutable domain records and the tracked rows.</summary>
public static class RowMapping
{
    public static EntitySnapshot ToState(this EntityRow row) =>
        new(
            row.Id, row.Version, row.Payload, row.PayloadChecksum, row.IsPublished, row.IsDisabledByAdmin, row.DisabledBy,
            row.DisabledAt, row.LastEventTimestamp, row.CreatedAt, row.UpdatedAt);

    public static EntityRow ToRow(this EntitySnapshot state)
    {
        var row = new EntityRow { Id = state.Id };
        state.CopyTo(row);
        return row;
    }

    /// <summary>Copies every field onto a tracked row; EF notices which ones actually changed.</summary>
    public static void CopyTo(this EntitySnapshot state, EntityRow row)
    {
        row.Version = state.Version;
        row.Payload = state.Payload;
        row.PayloadChecksum = state.PayloadChecksum;
        row.IsPublished = state.IsPublished;
        row.IsDisabledByAdmin = state.IsDisabledByAdmin;
        row.DisabledBy = state.DisabledBy;
        row.DisabledAt = state.DisabledAt;
        row.LastEventTimestamp = state.LastEventTimestamp;
        row.CreatedAt = state.CreatedAt;
        row.UpdatedAt = state.UpdatedAt;
    }

    public static TombstoneState ToState(this TombstoneRow row) => new(row.Id, row.DeletedAt, row.RecordedAt);
}
