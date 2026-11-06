namespace Lateral.Cms.Domain;

public enum CmsEventType
{
    Publish,
    UnPublish,
    Delete,
}

/// <summary>A validated, sanitised CMS event ready to be reduced into state.</summary>
/// <param name="EntityId">Trimmed entity id.</param>
/// <param name="Type">Publish, unPublish or delete.</param>
/// <param name="Version">CMS version. Null for delete events, which carry none.</param>
/// <param name="Timestamp">CMS event time, UTC.</param>
/// <param name="Payload">Canonical JSON entity fields. Null for delete events.</param>
/// <param name="PayloadChecksum">Checksum of <paramref name="Payload"/>. Null for delete events.</param>
public sealed record CmsEvent(
    string EntityId,
    CmsEventType Type,
    long? Version,
    DateTime Timestamp,
    string? Payload,
    string? PayloadChecksum = null);
