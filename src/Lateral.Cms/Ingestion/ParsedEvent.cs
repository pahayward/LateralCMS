using Lateral.Cms.Domain;

namespace Lateral.Cms.Ingestion;

/// <summary>
/// One element of an incoming batch after validation. Either <see cref="Event"/> is set (ready to be
/// applied) or <see cref="EarlyOutcome"/> says why it never will be (rejected / unsupported type).
/// The remaining fields are whatever could be read, kept for the response, ledger and logs.
/// </summary>
public sealed record ParsedEvent(
    int Index,
    string? EntityId,
    string? EventType,
    long? Version,
    DateTime? Timestamp,
    string? PayloadChecksum,
    CmsEvent? Event,
    EventOutcome? EarlyOutcome,
    string? Reason)
{
    public static ParsedEvent Valid(int index, string rawType, CmsEvent evt) =>
        new(index, evt.EntityId, rawType, evt.Version, evt.Timestamp, evt.PayloadChecksum, evt, null, null);

    public static ParsedEvent Rejected(
        int index, string? entityId, string? rawType, string reason, long? version = null, DateTime? timestamp = null) =>
        new(index, entityId, rawType, version, timestamp, null, null, EventOutcome.Rejected, reason);

    public static ParsedEvent Unsupported(int index, string? entityId, string rawType) =>
        new(index, entityId, rawType, null, null, null, null, EventOutcome.IgnoredUnsupportedType, ReasonCodes.UnsupportedType);
}
