namespace Lateral.Cms.Domain;

/// <summary>
/// Result of reducing one event: what happened, and the state that results. The states are the new values
/// (equal to the inputs when nothing changed); the caller compares them with what it started from.
/// </summary>
public readonly record struct Reduction(
    EventOutcome Outcome,
    string? Reason,
    EntitySnapshot? Entity,
    TombstoneState? Tombstone);

/// <summary>
/// The reconciliation rules for one entity id. Pure: no database, no clock, no I/O, no randomness, and nothing is
/// mutated; every call returns new values.
/// </summary>
/// <remarks>
/// <para>
/// Events are ordered per entity by <c>(version, timestamp)</c>; delete events carry no version and compare by
/// timestamp only. An event changes state only if it sorts after what is stored, so replays are no-ops and ties
/// are broken by content, never by arrival order.
/// </para>
/// <para>
/// Order-independence holds under one assumption (S17 in the design): for one id, a higher version never carries
/// an earlier timestamp than a lower one, and ids are not reused with restarting versions. Outside it, arrival
/// order can matter, because the two orderings (version for events, timestamp for deletes) can contradict each
/// other and no single stored row can remember enough to settle it. The counterexample is pinned by a test.
/// </para>
/// <para>
/// The admin override is carried over untouched by every CMS event, and a re-create after a delete starts
/// with it cleared.
/// </para>
/// </remarks>
public static class Reducer
{
    public static Reduction Apply(EntitySnapshot? entity, TombstoneState? tombstone, CmsEvent e, DateTime now) =>
        e.Type == CmsEventType.Delete
            ? ApplyDelete(entity, tombstone, e, now)
            : ApplyPublishOrUnpublish(entity, tombstone, e, now);

    private static Reduction ApplyPublishOrUnpublish(EntitySnapshot? current, TombstoneState? tombstone, CmsEvent e, DateTime now)
    {
        // Anything at or before the newest delete belongs to a lifetime that has ended.
        if (tombstone is { } tomb && e.Timestamp <= tomb.DeletedAt)
        {
            return new(EventOutcome.IgnoredStale, ReasonCodes.SupersededByDelete, current, tombstone);
        }

        var publish = e.Type == CmsEventType.Publish;

        // First sighting. For an unPublish this is the "X -> X+1 never published, then unpublished" case: the
        // event carries the whole entity, so it can be stored (hidden) straight away.
        if (current is null)
        {
            return new(EventOutcome.Applied, null, Create(e, now, publish), tombstone);
        }

        var version = e.Version!.Value;
        if (version < current.Version)
        {
            return new(EventOutcome.IgnoredStale, ReasonCodes.OlderVersion, current, tombstone);
        }

        if (version == current.Version)
        {
            var order = e.Timestamp.CompareTo(current.LastEventTimestamp);
            if (order < 0)
            {
                return new(EventOutcome.IgnoredStale, ReasonCodes.OlderTimestamp, current, tombstone);
            }

            if (order == 0)
            {
                if (publish == current.IsPublished)
                {
                    var content = string.CompareOrdinal(e.Payload, current.Payload);
                    if (content == 0)
                    {
                        return new(EventOutcome.IgnoredDuplicate, null, current, tombstone);
                    }

                    // Same version, instant and intent but different content: the CMS contradicted itself. Nothing
                    // in the events says which is right, so pick the same winner whatever the arrival order (the
                    // greater payload text) and flag it, rather than let arrival order decide.
                    return content < 0
                        ? new(EventOutcome.IgnoredStale, ReasonCodes.SameVersionDifferentContent, current, tombstone)
                        : new(EventOutcome.Applied, ReasonCodes.SameVersionDifferentContent, Overwrite(current, e, now, publish), tombstone);
                }

                if (publish)
                {
                    // Same version, same instant, opposite intent: fail closed.
                    return new(EventOutcome.IgnoredStale, ReasonCodes.TieUnpublishWins, current, tombstone);
                }
            }
        }

        return new(EventOutcome.Applied, null, Overwrite(current, e, now, publish), tombstone);
    }

    private static Reduction ApplyDelete(EntitySnapshot? current, TombstoneState? tombstone, CmsEvent e, DateTime now)
    {
        if (tombstone is { } tomb)
        {
            if (e.Timestamp < tomb.DeletedAt)
            {
                return new(EventOutcome.IgnoredStale, ReasonCodes.OlderTimestamp, current, tombstone);
            }

            if (e.Timestamp == tomb.DeletedAt)
            {
                return new(EventOutcome.IgnoredDuplicate, null, current, tombstone);
            }

            tombstone = tomb with { DeletedAt = e.Timestamp, RecordedAt = now };
        }
        else
        {
            tombstone = new TombstoneState(e.EntityId, e.Timestamp, now);
        }

        if (current is null)
        {
            // Nothing stored (yet): the tombstone alone makes older events harmless if they arrive later.
            return new(EventOutcome.Applied, ReasonCodes.TombstoneOnly, null, tombstone);
        }

        if (e.Timestamp < current.LastEventTimestamp)
        {
            // The entity was (re)published after this delete happened. The tombstone high-water mark is still
            // raised so the final state is the same whatever order the events arrived in.
            return new(EventOutcome.IgnoredStale, ReasonCodes.NewerStateExists, current, tombstone);
        }

        return new(EventOutcome.Applied, null, null, tombstone);
    }

    private static EntitySnapshot Create(CmsEvent e, DateTime now, bool publish) =>
        new(
            e.EntityId,
            e.Version!.Value,
            e.Payload!,
            e.PayloadChecksum ?? "",
            publish,
            IsDisabledByAdmin: false,
            DisabledBy: null,
            DisabledAt: null,
            e.Timestamp,
            CreatedAt: now,
            UpdatedAt: now);

    private static EntitySnapshot Overwrite(EntitySnapshot current, CmsEvent e, DateTime now, bool publish) =>
        current with
        {
            Version = e.Version!.Value,
            Payload = e.Payload!,
            PayloadChecksum = e.PayloadChecksum ?? "",
            IsPublished = publish,
            LastEventTimestamp = e.Timestamp,
            UpdatedAt = now,
        };
}
