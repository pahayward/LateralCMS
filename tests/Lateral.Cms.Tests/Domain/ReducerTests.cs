using Lateral.Cms.Domain;

namespace Lateral.Cms.Tests.Domain;

/// <summary>One test per row of the reconciliation table (design section 4.4), with both arrival orders for every tie.</summary>
public class ReducerTests
{
    private static readonly DateTime Now = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int minute) => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute);

    private static CmsEvent Pub(long version, int minute, string? payload = null, string id = "x") =>
        new(id, CmsEventType.Publish, version, At(minute), payload ?? $"{{\"v\":{version}}}", $"sha256:{payload ?? version.ToString()}");

    private static CmsEvent Unpub(long version, int minute, string? payload = null, string id = "x") =>
        new(id, CmsEventType.UnPublish, version, At(minute), payload ?? $"{{\"v\":{version}}}", $"sha256:{payload ?? version.ToString()}");

    private static CmsEvent Del(int minute, string id = "x") => new(id, CmsEventType.Delete, null, At(minute), null);

    private sealed record Folded(EntitySnapshot? Entity, TombstoneState? Tombstone, List<(EventOutcome Outcome, string? Reason)> Steps);

    private static Folded Fold(params CmsEvent[] events) => Fold(null, null, events);

    private static Folded Fold(EntitySnapshot? entity, TombstoneState? tombstone, params CmsEvent[] events)
    {
        var steps = new List<(EventOutcome, string?)>();
        foreach (var e in events)
        {
            var r = Reducer.Apply(entity, tombstone, e, Now);
            entity = r.Entity;
            tombstone = r.Tombstone;
            steps.Add((r.Outcome, r.Reason));
        }

        return new Folded(entity, tombstone, steps);
    }

    // ---- EV-01 ----

    [Fact]
    [Trait("req", "EV-01")]
    public void A_publish_for_an_unknown_id_creates_it_published_with_the_whole_payload()
    {
        var result = Fold(Pub(1, 0, "{\"a\":1}"));

        Assert.Equal(EventOutcome.Applied, result.Steps[0].Outcome);
        var entity = Assert.IsType<EntitySnapshot>(result.Entity);
        Assert.True(entity.IsPublished);
        Assert.Equal(1, entity.Version);
        Assert.Equal("{\"a\":1}", entity.Payload);
        Assert.Equal(Now, entity.CreatedAt);
    }

    [Fact]
    [Trait("req", "EV-01")]
    public void A_later_publish_replaces_the_payload_and_never_merges_fields()
    {
        var result = Fold(Pub(1, 0, "{\"a\":1,\"b\":2}"), Pub(2, 1, "{\"c\":3}"));

        Assert.Equal("{\"c\":3}", result.Entity!.Payload);
    }

    // ---- EV-02 ----

    [Fact]
    [Trait("req", "EV-02")]
    public void A_higher_version_replaces_version_payload_and_checksum()
    {
        var result = Fold(Pub(1, 0, "{\"a\":1}"), Pub(3, 1, "{\"a\":3}"));

        Assert.Equal(EventOutcome.Applied, result.Steps[1].Outcome);
        Assert.Equal(3, result.Entity!.Version);
        Assert.Equal("{\"a\":3}", result.Entity.Payload);
        Assert.Equal("sha256:{\"a\":3}", result.Entity.PayloadChecksum);
    }

    [Fact]
    [Trait("req", "EV-02")]
    [Trait("req", "EV-14")]
    public void A_lower_version_is_ignored_as_stale_even_when_it_arrives_later_with_a_later_timestamp()
    {
        var result = Fold(Pub(5, 0), Pub(3, 10));

        Assert.Equal((EventOutcome.IgnoredStale, ReasonCodes.OlderVersion), result.Steps[1]);
        Assert.Equal(5, result.Entity!.Version);
    }

    [Fact]
    [Trait("req", "EV-02")]
    public void The_same_version_with_a_newer_timestamp_applies_so_state_can_flip_back_to_published()
    {
        var result = Fold(Pub(2, 0), Unpub(2, 1), Pub(2, 2));

        Assert.All(result.Steps, s => Assert.Equal(EventOutcome.Applied, s.Outcome));
        Assert.True(result.Entity!.IsPublished);
    }

    [Fact]
    [Trait("req", "EV-02")]
    public void The_same_version_with_an_older_timestamp_is_stale()
    {
        var result = Fold(Pub(2, 5), Unpub(2, 1));

        Assert.Equal((EventOutcome.IgnoredStale, ReasonCodes.OlderTimestamp), result.Steps[1]);
        Assert.True(result.Entity!.IsPublished);
    }

    // ---- EV-03 ----

    [Fact]
    [Trait("req", "EV-03")]
    public void An_exact_duplicate_is_ignored_and_changes_nothing()
    {
        var first = Fold(Pub(2, 0, "{\"a\":1}"));
        var replay = Fold(first.Entity, first.Tombstone, Pub(2, 0, "{\"a\":1}"));

        Assert.Equal((EventOutcome.IgnoredDuplicate, (string?)null), replay.Steps[0]);
        Assert.Equal(first.Entity, replay.Entity);
    }

    [Fact]
    [Trait("req", "EV-03")]
    public void Replaying_a_whole_stream_changes_nothing()
    {
        var stream = new[] { Pub(1, 0), Pub(2, 1), Unpub(3, 2), Del(3), Pub(1, 4, id: "x") };
        var once = Fold(stream);
        var twice = Fold(once.Entity, once.Tombstone, stream);

        Assert.Equal(once.Entity, twice.Entity);
        Assert.Equal(once.Tombstone, twice.Tombstone);
        Assert.All(twice.Steps, s => Assert.NotEqual(EventOutcome.Applied, s.Outcome));
    }

    // ---- EV-04, EV-05 ----

    [Fact]
    [Trait("req", "EV-04")]
    public void An_unpublish_keeps_the_row_and_payload_and_marks_it_unpublished()
    {
        var result = Fold(Pub(1, 0, "{\"a\":1}"), Unpub(1, 1, "{\"a\":1}"));

        Assert.Equal(EventOutcome.Applied, result.Steps[1].Outcome);
        Assert.False(result.Entity!.IsPublished);
        Assert.Equal("{\"a\":1}", result.Entity.Payload);
    }

    [Fact]
    [Trait("req", "EV-05")]
    public void An_unpublish_for_an_id_never_seen_creates_it_unpublished_from_the_events_payload()
    {
        // X was published as v1, edited to v2 (never published), then v2 was unpublished: the service only ever hears about v2.
        var result = Fold(Unpub(2, 0, "{\"title\":\"draft\"}"));

        Assert.Equal(EventOutcome.Applied, result.Steps[0].Outcome);
        Assert.False(result.Entity!.IsPublished);
        Assert.Equal(2, result.Entity.Version);
        Assert.Equal("{\"title\":\"draft\"}", result.Entity.Payload);
    }

    [Fact]
    [Trait("req", "EV-05")]
    public void The_unpublish_of_an_unseen_id_wins_over_a_late_older_publish_in_either_order()
    {
        var a = Fold(Unpub(2, 5), Pub(1, 1));
        var b = Fold(Pub(1, 1), Unpub(2, 5));

        Assert.NotNull(a.Entity);
        Assert.Equal(a.Entity, b.Entity);
        Assert.False(a.Entity.IsPublished);
        Assert.Equal(2, a.Entity.Version);
    }

    // ---- EV-06, EV-07, EV-08 ----

    [Fact]
    [Trait("req", "EV-06")]
    public void A_delete_removes_the_entity_and_leaves_a_payload_free_tombstone()
    {
        var result = Fold(Pub(1, 0), Del(5));

        Assert.Equal(EventOutcome.Applied, result.Steps[1].Outcome);
        Assert.Null(result.Entity);
        Assert.Equal(new TombstoneState("x", At(5), Now), result.Tombstone);
    }

    [Fact]
    [Trait("req", "EV-07")]
    public void Events_at_or_before_the_tombstone_are_stale_so_a_deleted_entity_cannot_come_back()
    {
        var deleted = Fold(Pub(1, 0), Del(5));

        foreach (var late in new[] { Pub(9, 5), Pub(9, 4), Unpub(9, 3) })
        {
            var result = Fold(deleted.Entity, deleted.Tombstone, late);
            Assert.Equal((EventOutcome.IgnoredStale, ReasonCodes.SupersededByDelete), result.Steps[0]);
            Assert.Null(result.Entity);
        }
    }

    [Fact]
    [Trait("req", "EV-07")]
    [Trait("req", "EV-15")]
    public void A_publish_after_the_delete_recreates_the_entity_with_the_admin_override_cleared()
    {
        var disabled = Fold(Pub(1, 0)).Entity! with { IsDisabledByAdmin = true, DisabledBy = "admin", DisabledAt = Now };
        var deleted = Fold(disabled, null, Del(5));

        var result = Fold(deleted.Entity, deleted.Tombstone, Pub(1, 6));

        Assert.Equal(EventOutcome.Applied, result.Steps[0].Outcome);
        Assert.NotNull(result.Entity);
        Assert.False(result.Entity.IsDisabledByAdmin);
        Assert.Null(result.Entity.DisabledBy);
        Assert.Null(result.Entity.DisabledAt);
    }

    [Fact]
    [Trait("req", "EV-08")]
    public void A_delete_with_nothing_stored_records_a_tombstone_only()
    {
        var result = Fold(Del(5));

        Assert.Equal((EventOutcome.Applied, ReasonCodes.TombstoneOnly), result.Steps[0]);
        Assert.Null(result.Entity);
        Assert.Equal(At(5), result.Tombstone!.DeletedAt);
    }

    [Fact]
    [Trait("req", "EV-08")]
    public void A_delete_older_than_the_stored_state_is_stale_but_still_raises_the_tombstone()
    {
        var result = Fold(Pub(2, 10), Del(5));

        Assert.Equal((EventOutcome.IgnoredStale, ReasonCodes.NewerStateExists), result.Steps[1]);
        Assert.NotNull(result.Entity);
        Assert.Equal(At(5), result.Tombstone!.DeletedAt);
    }

    [Fact]
    [Trait("req", "EV-08")]
    public void A_delete_at_or_below_the_existing_tombstone_is_a_duplicate_or_stale()
    {
        var first = Fold(Del(5));

        Assert.Equal((EventOutcome.IgnoredDuplicate, (string?)null), Fold(first.Entity, first.Tombstone, Del(5)).Steps[0]);
        Assert.Equal((EventOutcome.IgnoredStale, ReasonCodes.OlderTimestamp), Fold(first.Entity, first.Tombstone, Del(3)).Steps[0]);
    }

    [Fact]
    [Trait("req", "EV-08")]
    public void A_later_delete_raises_the_tombstone_high_water_mark()
    {
        var result = Fold(Del(5), Del(9));

        Assert.Equal((EventOutcome.Applied, ReasonCodes.TombstoneOnly), result.Steps[1]);
        Assert.Equal(At(9), result.Tombstone!.DeletedAt);
    }

    // ---- EV-10 ----

    [Fact]
    [Trait("req", "EV-10")]
    public void An_unpublish_beats_a_publish_at_the_same_version_and_instant_in_either_arrival_order()
    {
        var publishFirst = Fold(Pub(2, 4), Unpub(2, 4));
        var unpublishFirst = Fold(Unpub(2, 4), Pub(2, 4));

        Assert.False(publishFirst.Entity!.IsPublished);
        Assert.False(unpublishFirst.Entity!.IsPublished);
        Assert.Equal((EventOutcome.IgnoredStale, ReasonCodes.TieUnpublishWins), unpublishFirst.Steps[1]);
        Assert.Equal(EventOutcome.Applied, publishFirst.Steps[1].Outcome);
    }

    [Fact]
    [Trait("req", "EV-10")]
    public void Contradicting_content_at_the_same_version_instant_and_kind_is_decided_by_the_greater_text_not_by_arrival()
    {
        var low = Pub(2, 4, "{\"t\":\"a\"}");
        var high = Pub(2, 4, "{\"t\":\"b\"}");

        var lowFirst = Fold(low, high);
        var highFirst = Fold(high, low);

        Assert.Equal("{\"t\":\"b\"}", lowFirst.Entity!.Payload);
        Assert.Equal("{\"t\":\"b\"}", highFirst.Entity!.Payload);
        Assert.Equal((EventOutcome.Applied, ReasonCodes.SameVersionDifferentContent), lowFirst.Steps[1]);
        Assert.Equal((EventOutcome.IgnoredStale, ReasonCodes.SameVersionDifferentContent), highFirst.Steps[1]);
    }

    [Fact]
    [Trait("req", "EV-10")]
    public void Contradicting_unpublishes_are_decided_the_same_way()
    {
        var a = Fold(Unpub(2, 4, "{\"t\":\"a\"}"), Unpub(2, 4, "{\"t\":\"b\"}"));
        var b = Fold(Unpub(2, 4, "{\"t\":\"b\"}"), Unpub(2, 4, "{\"t\":\"a\"}"));

        Assert.Equal(a.Entity!.Payload, b.Entity!.Payload);
        Assert.Equal("{\"t\":\"b\"}", a.Entity.Payload);
    }

    // ---- EV-15 ----

    [Fact]
    [Trait("req", "EV-15")]
    public void A_cms_event_never_touches_the_admin_override()
    {
        var disabled = Fold(Pub(1, 0)).Entity! with { IsDisabledByAdmin = true, DisabledBy = "admin", DisabledAt = Now };

        foreach (var e in new[] { Pub(2, 1), Unpub(3, 2), Pub(4, 3) })
        {
            var result = Fold(disabled, null, e);
            Assert.Equal(EventOutcome.Applied, result.Steps[0].Outcome);
            Assert.True(result.Entity!.IsDisabledByAdmin);
            Assert.Equal("admin", result.Entity.DisabledBy);
            disabled = result.Entity;
        }
    }

    // ---- Purity ----

    [Fact]
    [Trait("req", "EV-09")]
    public void The_reducer_is_pure_the_same_inputs_give_equal_outputs_and_inputs_are_not_changed()
    {
        var entity = Fold(Pub(1, 0)).Entity!;
        var snapshot = entity with { };
        var tombstone = new TombstoneState("x", At(-5), Now);

        var first = Reducer.Apply(entity, tombstone, Pub(2, 1), Now);
        var second = Reducer.Apply(entity, tombstone, Pub(2, 1), Now);

        Assert.Equal(first, second);
        Assert.Equal(snapshot, entity);
        Assert.NotSame(entity, first.Entity);
    }
}
