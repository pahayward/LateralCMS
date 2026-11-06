namespace Lateral.Cms.Domain;

/// <summary>What happened to one event of a batch.</summary>
public enum EventOutcome
{
    /// <summary>Accepted and stored durably, but not decided yet. The only outcome a payload may still exist under.</summary>
    Queued,

    /// <summary>The event changed the stored state.</summary>
    Applied,

    /// <summary>The event is older than what is stored (out-of-order delivery or superseded by a delete).</summary>
    IgnoredStale,

    /// <summary>The event matches what is already stored (replay).</summary>
    IgnoredDuplicate,

    /// <summary>The event type is not one this service understands.</summary>
    IgnoredUnsupportedType,

    /// <summary>The event failed validation and was not processed.</summary>
    Rejected,
}

/// <summary>Stable machine-readable reasons reported alongside an outcome. Never reworded.</summary>
public static class ReasonCodes
{
    // Reducer reasons.
    public const string OlderVersion = "older_version";
    public const string OlderTimestamp = "older_timestamp";
    public const string TieUnpublishWins = "tie_unpublish_wins";
    public const string SupersededByDelete = "superseded_by_delete";
    public const string NewerStateExists = "newer_state_exists";
    public const string TombstoneOnly = "tombstone_only";
    public const string SameVersionDifferentContent = "same_version_different_content";

    // Validation reasons.
    public const string EventNotObject = "event_not_object";
    public const string MalformedEvent = "malformed_event";
    public const string MissingType = "missing_type";
    public const string UnsupportedType = "unsupported_type";
    public const string MissingId = "missing_id";
    public const string InvalidId = "invalid_id";
    public const string MissingTimestamp = "missing_timestamp";
    public const string InvalidTimestamp = "invalid_timestamp";
    public const string TimestampInFuture = "timestamp_in_future";
    public const string MissingVersion = "missing_version";
    public const string InvalidVersion = "invalid_version";
    public const string MissingPayload = "missing_payload";
    public const string InvalidPayload = "invalid_payload";
    public const string PayloadTooLarge = "payload_too_large";
}
