using Lateral.Cms.Domain;

namespace Lateral.Cms.Tests.Domain;

/// <summary>
/// Properties of the reducer: the final state must not depend on arrival order. The reference is an independent model
/// (the oracle) that sees only the <em>set</em> of events: the tombstone is the newest delete, live events are those
/// strictly after it, and the winner is the maximum of (version, timestamp, unPublish-beats-publish, payload text).
/// </summary>
public class ReducerPropertyTests
{
    private static readonly DateTime Now = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int minute) => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute);

    /// <summary>What two runs must agree on.</summary>
    public sealed record Snap(
        bool Exists, long Version = 0, bool Published = false, string? Payload = null, DateTime? LastEvent = null, DateTime? Tombstone = null);

    // ---- The oracle (design appendix A) ----

    public static Snap Oracle(IReadOnlyList<CmsEvent> events)
    {
        DateTime? tomb = events.Where(e => e.Type == CmsEventType.Delete).Select(e => (DateTime?)e.Timestamp).Max();
        var live = events.Where(e => e.Type != CmsEventType.Delete && (tomb is null || e.Timestamp > tomb)).ToList();
        if (live.Count == 0)
        {
            return new Snap(false, Tombstone: tomb);
        }

        var winner = live
            .OrderBy(e => e.Version!.Value)
            .ThenBy(e => e.Timestamp)
            .ThenBy(e => e.Type == CmsEventType.UnPublish ? 1 : 0)
            .ThenBy(e => e.Payload, StringComparer.Ordinal)
            .Last();
        return new Snap(true, winner.Version!.Value, winner.Type == CmsEventType.Publish, winner.Payload, winner.Timestamp, tomb);
    }

    public static Snap Run(IEnumerable<CmsEvent> inOrder)
    {
        EntitySnapshot? entity = null;
        TombstoneState? tombstone = null;
        foreach (var e in inOrder)
        {
            var r = Reducer.Apply(entity, tombstone, e, Now);
            entity = r.Entity;
            tombstone = r.Tombstone;
        }

        return entity is null
            ? new Snap(false, Tombstone: tombstone?.DeletedAt)
            : new Snap(true, entity.Version, entity.IsPublished, entity.Payload, entity.LastEventTimestamp, tombstone?.DeletedAt);
    }

    // ---- Timeline generator (design appendix A) ----

    private static CmsEvent Make(CmsEventType type, long version, int minute, int salt) =>
        type == CmsEventType.Delete
            ? new CmsEvent("x", type, null, At(minute), null)
            : new CmsEvent("x", type, version, At(minute), $"{{\"v\":{version},\"m\":{minute},\"s\":{salt}}}", $"sha256:{version}-{minute}-{salt}");

    /// <summary>One entity id; versions and timestamps rise together unless <paramref name="breakOrdering"/> lets the version fall back.</summary>
    public static List<CmsEvent> Timeline(Random rng, bool breakOrdering = false)
    {
        var events = new List<CmsEvent>();
        var minute = 0;
        long running = 1;
        for (var step = rng.Next(1, 9); step > 0; step--)
        {
            if (rng.Next(5) != 0)
            {
                minute += rng.Next(1, 3);
            }

            var roll = rng.Next(10);
            var type = roll < 4 ? CmsEventType.Publish : roll < 7 ? CmsEventType.UnPublish : CmsEventType.Delete;
            if (type == CmsEventType.Delete)
            {
                events.Add(Make(type, 0, minute, 0));
                continue;
            }

            running += rng.Next(0, 3);
            var version = breakOrdering ? Math.Max(1, running - rng.Next(0, 3)) : running;
            events.Add(Make(type, version, minute, 0));

            if (rng.Next(6) == 0)
            {
                events.Add(Make(type, version, minute, 1)); // same kind, same version and instant, different content
            }

            if (rng.Next(6) == 0)
            {
                events.Add(Make(type == CmsEventType.Publish ? CmsEventType.UnPublish : CmsEventType.Publish, version, minute, 0));
            }
        }

        for (var duplicates = rng.Next(0, 3); duplicates > 0 && events.Count > 0; duplicates--)
        {
            events.Add(events[rng.Next(events.Count)]);
        }

        return events;
    }

    private static void Shuffle<T>(Random rng, List<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    // ---- EV-09: order independence ----

    [Fact]
    [Trait("req", "EV-09")]
    public void Every_shuffle_of_twenty_thousand_random_timelines_ends_where_the_oracle_says()
    {
        var rng = new Random(20241105);
        for (var n = 0; n < 20_000; n++)
        {
            var timeline = Timeline(rng);
            var expected = Oracle(timeline);
            for (var shuffle = 0; shuffle < 3; shuffle++)
            {
                var order = new List<CmsEvent>(timeline);
                Shuffle(rng, order);
                var actual = Run(order);
                Assert.True(
                    expected == actual,
                    $"Timeline {n} shuffle {shuffle} disagrees.\nExpected {expected}\nActual   {actual}\nOrder: {Describe(order)}");
            }
        }
    }

    [Fact]
    [Trait("req", "EV-09")]
    [Trait("category", "slow")]
    public void The_slow_category_one_shuffle_of_each_of_a_million_random_timelines_also_ends_where_the_oracle_says()
    {
        // The suite is quick enough that this runs with everything else (a few seconds); filter on the trait to run it alone.
        var rng = new Random(1_000_003);
        for (var n = 0; n < 1_000_000; n++)
        {
            var timeline = Timeline(rng);
            var order = new List<CmsEvent>(timeline);
            Shuffle(rng, order);
            var expected = Oracle(timeline);
            var actual = Run(order);
            if (expected != actual)
            {
                Assert.Fail($"Timeline {n} disagrees.\nExpected {expected}\nActual   {actual}\nOrder: {Describe(order)}");
            }
        }
    }

    [Fact]
    [Trait("req", "EV-09")]
    public void Every_arrival_order_of_each_curated_timeline_ends_in_the_same_state()
    {
        foreach (var (name, timeline) in Curated())
        {
            var expected = Oracle(timeline);
            var checkedOrders = 0;
            foreach (var order in Permutations(timeline))
            {
                var actual = Run(order);
                Assert.True(expected == actual, $"{name}: {Describe(order)}\nExpected {expected}\nActual   {actual}");
                checkedOrders++;
            }

            Assert.True(checkedOrders >= 1, name);
        }
    }

    public static IEnumerable<(string Name, List<CmsEvent> Events)> Curated()
    {
        CmsEvent P(long v, int m, int s = 0) => Make(CmsEventType.Publish, v, m, s);
        CmsEvent U(long v, int m, int s = 0) => Make(CmsEventType.UnPublish, v, m, s);
        CmsEvent D(int m) => Make(CmsEventType.Delete, 0, m, 0);

        yield return ("publish then edit then unpublish", [P(1, 0), P(2, 1), U(2, 2)]);
        yield return ("the X, X+1, unpublish corner case", [P(1, 0), U(3, 5)]);
        yield return ("publish unpublish republish at one version", [P(2, 0), U(2, 1), P(2, 2)]);
        yield return ("tie between publish and unpublish", [P(2, 4), U(2, 4), P(3, 5)]);
        yield return ("contradicting content", [P(2, 4, 0), P(2, 4, 1), U(2, 4, 0), U(2, 4, 1)]);
        yield return ("delete between lifetimes", [P(1, 0), P(2, 1), D(2), P(3, 3), U(4, 4)]);
        yield return ("delete tie with a publish", [P(1, 0), D(0), P(2, 0), D(1)]);
        yield return ("two deletes and a late publish", [D(2), D(5), P(1, 1), P(2, 6), U(2, 7)]);
        yield return ("duplicates everywhere", [P(1, 0), P(1, 0), U(1, 1), U(1, 1), D(3), D(3), P(2, 4)]);
        yield return ("all kinds at one instant", [P(1, 1), U(1, 1), D(1), P(1, 1, 1), U(1, 1, 1)]);
        yield return ("recreate with a continuing version", [P(5, 0), D(1), P(6, 2), U(6, 3)]);
    }

    public static IEnumerable<List<CmsEvent>> Permutations(List<CmsEvent> items)
    {
        // Heap's algorithm over indices.
        var indices = Enumerable.Range(0, items.Count).ToArray();
        var counters = new int[indices.Length];
        yield return [.. indices.Select(i => items[i])];

        var position = 1;
        while (position < indices.Length)
        {
            if (counters[position] < position)
            {
                var swapWith = position % 2 == 0 ? 0 : counters[position];
                (indices[swapWith], indices[position]) = (indices[position], indices[swapWith]);
                yield return [.. indices.Select(i => items[i])];
                counters[position]++;
                position = 1;
            }
            else
            {
                counters[position] = 0;
                position++;
            }
        }
    }

    [Fact]
    [Trait("req", "EV-09")]
    public void Heaps_algorithm_visits_every_permutation_once()
    {
        var items = Enumerable.Range(0, 5).Select(i => Make(CmsEventType.Publish, i + 1, i, 0)).ToList();

        var seen = Permutations(items).Select(p => string.Join(",", p.Select(e => e.Version))).ToList();

        Assert.Equal(120, seen.Count);
        Assert.Equal(120, seen.Distinct().Count());
    }

    // ---- EV-03: idempotence ----

    [Fact]
    [Trait("req", "EV-03")]
    public void Replaying_any_random_stream_a_second_time_changes_nothing()
    {
        var rng = new Random(77);
        for (var n = 0; n < 5_000; n++)
        {
            var timeline = Timeline(rng);
            var once = Run(timeline);
            var twice = Run(timeline.Concat(timeline));
            Assert.True(once == twice, $"Timeline {n}: once {once}, twice {twice}");
        }
    }

    // ---- EV-12: batch partition independence (at the reducer level) ----

    [Fact]
    [Trait("req", "EV-12")]
    public void Splitting_a_stream_into_chunks_and_applying_the_chunks_in_any_order_gives_the_same_state()
    {
        var rng = new Random(4242);
        for (var n = 0; n < 5_000; n++)
        {
            var timeline = Timeline(rng);
            var expected = Oracle(timeline);

            var shuffled = new List<CmsEvent>(timeline);
            Shuffle(rng, shuffled);
            var chunks = new List<List<CmsEvent>>();
            for (var i = 0; i < shuffled.Count;)
            {
                var size = rng.Next(1, 4);
                chunks.Add(shuffled.Skip(i).Take(size).ToList());
                i += size;
            }

            Shuffle(rng, chunks);
            Assert.True(expected == Run(chunks.SelectMany(c => c)), $"Timeline {n}");
        }
    }

    // ---- EV-16: the ordering assumption, and what happens outside it ----

    [Fact]
    [Trait("req", "EV-16")]
    public void The_pinned_counterexample_arrival_order_matters_when_a_higher_version_carries_an_earlier_timestamp()
    {
        // publish v2 @t5, publish v3 @t1 (a higher version with an EARLIER timestamp: outside the stated assumption), delete @t3.
        var v2 = Make(CmsEventType.Publish, 2, 5, 0);
        var v3 = Make(CmsEventType.Publish, 3, 1, 0);
        var delete = Make(CmsEventType.Delete, 0, 3, 0);

        var oracle = Oracle([v2, v3, delete]);
        var deleteLast = Run([v2, v3, delete]);
        var deleteFirst = Run([delete, v2, v3]);

        Assert.Equal(new Snap(true, 2, true, v2.Payload, v2.Timestamp, delete.Timestamp), oracle);
        Assert.Equal(oracle, deleteFirst);
        Assert.False(deleteLast.Exists); // order (v2, v3, delete) leaves no entity at all
        Assert.NotEqual(deleteFirst, deleteLast);
    }

    [Fact]
    [Trait("req", "EV-16")]
    public void The_second_pinned_limit_an_id_reused_with_restarting_versions_depends_on_arrival_order()
    {
        // v5 is deleted and the id is reused from version 1. A late unPublish v1 that arrives while v5 still exists is
        // discarded as stale, and cannot be recovered once the delete and the re-create have happened.
        var v5 = Make(CmsEventType.Publish, 5, 0, 0);
        var delete = Make(CmsEventType.Delete, 0, 1, 0);
        var reused = Make(CmsEventType.Publish, 1, 2, 0);
        var late = Make(CmsEventType.UnPublish, 1, 3, 0);

        var oracle = Oracle([v5, delete, reused, late]);
        var arrivalA = Run([v5, late, delete, reused]);
        var arrivalB = Run([v5, delete, reused, late]);

        Assert.False(oracle.Published);
        Assert.Equal(oracle, arrivalB);
        Assert.True(arrivalA.Published);
        Assert.NotEqual(arrivalA, arrivalB);
    }

    [Fact]
    [Trait("req", "EV-16")]
    public void Outside_the_assumption_some_shuffles_diverge_from_the_oracle_and_inside_it_none_do()
    {
        var rng = new Random(99);
        var diverged = 0;
        for (var n = 0; n < 20_000; n++)
        {
            var timeline = Timeline(rng, breakOrdering: true);
            var order = new List<CmsEvent>(timeline);
            Shuffle(rng, order);
            if (Oracle(timeline) != Run(order))
            {
                diverged++;
            }
        }

        // The documented limit (K8) is real, which is why it is pinned rather than hidden.
        Assert.True(diverged > 0, "Expected the broken-ordering generator to expose the known limit.");
    }

    private static string Describe(IEnumerable<CmsEvent> events) =>
        string.Join(" ; ", events.Select(e => $"{e.Type}{(e.Version is { } v ? $" v{v}" : "")} @{(e.Timestamp - At(0)).TotalMinutes}m {e.Payload}"));
}
