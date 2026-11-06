namespace Lateral.Cms.Domain;

/// <summary>
/// The latest known state of one CMS entity (a projection of the CMS, not a version history). Immutable: the
/// reducer returns a new value and the persistence layer decides which rows to insert, update or delete.
/// All timestamps are UTC.
/// </summary>
/// <param name="Id">Identifier assigned by the CMS.</param>
/// <param name="Version">CMS version the payload belongs to.</param>
/// <param name="Payload">Canonical JSON text of the entity fields.</param>
/// <param name="PayloadChecksum">Checksum of exactly <paramref name="Payload"/>, computed when it was ingested.</param>
/// <param name="IsPublished">CMS state: true after a publish, false after an unPublish.</param>
/// <param name="IsDisabledByAdmin">Local admin override. CMS events never write it.</param>
/// <param name="DisabledBy">Who set the override.</param>
/// <param name="DisabledAt">When the override was set.</param>
/// <param name="LastEventTimestamp">CMS timestamp of the event that produced this state (ordering tiebreaker).</param>
/// <param name="CreatedAt">When this lifetime of the entity began.</param>
/// <param name="UpdatedAt">When the state last changed.</param>
public sealed record EntitySnapshot(
    string Id,
    long Version,
    string Payload,
    string PayloadChecksum,
    bool IsPublished,
    bool IsDisabledByAdmin,
    string? DisabledBy,
    DateTime? DisabledAt,
    DateTime LastEventTimestamp,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Marker left behind by a hard delete. Holds no entity data, only the id and the CMS timestamp of the newest
/// delete seen, so that late or replayed events cannot resurrect a deleted entity.
/// </summary>
/// <param name="Id">Entity id.</param>
/// <param name="DeletedAt">CMS timestamp of the newest delete for this id (a high-water mark).</param>
/// <param name="RecordedAt">When this service last raised the mark.</param>
public sealed record TombstoneState(string Id, DateTime DeletedAt, DateTime RecordedAt);
