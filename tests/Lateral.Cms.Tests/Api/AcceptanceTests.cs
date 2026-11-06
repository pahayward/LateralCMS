using System.Net;
using System.Text.Json;
using Lateral.Cms.Domain;
using Lateral.Cms.Tests.Domain;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Api;

/// <summary>
/// The accepting half of the pipeline: what the sender is told, and what is (and is not) stored when it is told.
/// The worker is off, so each test applies the queue itself, deliberately.
/// </summary>
public class AcceptanceTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private HttpClient Admin => factory.Admin();

    private async Task<string[]> OutcomesAsync(string batchId)
    {
        using var status = await factory.StatusAsync(batchId);
        return status.Outcomes();
    }

    private long Rows(string table, string batchId) =>
        factory.Count($"SELECT COUNT(*) FROM {table} WHERE BatchId = '{batchId}' COLLATE NOCASE");

    // ---- the acknowledgement -----------------------------------------------------------------------------

    [Fact]
    [Trait("req", "AS-01")]
    [Trait("req", "AS-06")]
    public async Task A_batch_is_acknowledged_with_202_and_a_way_to_follow_it_before_anything_is_applied()
    {
        await factory.DrainAsync();
        var id = Evt.Id();

        var response = await factory.Cms().PostAsync(Evt.Publish(id, 1, 1));
        var body = await response.ReadJsonAsync();
        var batchId = CmsFactory.BatchId(body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/cms/batches/{batchId}", response.Headers.Location?.ToString());
        Assert.Equal(batchId, response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("queued").GetInt32());
        Assert.Equal(["queued"], body.Outcomes());
        Assert.StartsWith("sha256:", body.RootElement.GetProperty("results")[0].GetProperty("checksum").GetString());

        // Safe, but not applied: the entity does not exist for anyone yet, and the work is on disk.
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(id)).StatusCode);
        Assert.Equal(1, Rows("Batches", batchId));
        Assert.Equal(1, Rows("Events", batchId));
        Assert.Equal("queued", (string)factory.Sql($"SELECT Outcome FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE")[0][0]!);
        Assert.Equal("queued", (string)factory.Sql($"SELECT Status FROM Batches WHERE BatchId = '{batchId}' COLLATE NOCASE")[0][0]!);
        await factory.DrainAsync();
    }

    [Fact]
    [Trait("req", "IN-07")]
    public async Task The_acknowledged_checksum_is_the_one_stored_once_the_batch_is_applied()
    {
        var id = Evt.Id();
        using var ack = await factory.AcceptAsync(Evt.Publish(id, 1, 1, new { title = "checksummed" }));
        var promised = ack.RootElement.GetProperty("results")[0].GetProperty("checksum").GetString();

        await factory.DrainAsync();

        using var stored = await (await Admin.GetEntityAsync(id)).ReadJsonAsync();
        Assert.Equal(promised, stored.RootElement.GetProperty("checksum").GetString());
    }

    // ---- what is stored while it waits ---------------------------------------------------------------------

    [Fact]
    [Trait("req", "AS-09")]
    public async Task A_payload_waits_in_its_event_row_and_is_gone_once_the_batch_is_applied()
    {
        var id = Evt.Id();
        var batchId = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(id, 1, 1, new { title = "queue-sentinel" })));

        Assert.Contains("queue-sentinel", (string)factory.Sql($"SELECT Payload FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE")[0][0]!);

        await factory.DrainAsync();

        // The row stays as the ledger entry, the content does not.
        Assert.Null(factory.Sql($"SELECT Payload FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE")[0][0]);
        Assert.Equal("applied", (string)factory.Sql($"SELECT Outcome FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE")[0][0]!);
        Assert.Equal("completed", (string)factory.Sql($"SELECT Status FROM Batches WHERE BatchId = '{batchId}' COLLATE NOCASE")[0][0]!);
        Assert.Equal(1, factory.Count($"SELECT COUNT(*) FROM Entities WHERE Id = '{id}'"));
        using var entity = await (await Admin.GetEntityAsync(id)).ReadJsonAsync();
        Assert.Equal("queue-sentinel", entity.RootElement.GetProperty("payload").GetProperty("title").GetString());
    }

    [Fact]
    [Trait("req", "AS-09")]
    [Trait("req", "EV-06")]
    public async Task A_delete_leaves_no_copy_of_an_earlier_publish_anywhere_once_both_are_applied()
    {
        var id = Evt.Id();
        await factory.AcceptAsync(Evt.Publish(id, 1, 1, new { title = "must-not-survive-the-delete" }));
        await factory.AcceptAsync(Evt.Delete(id, 2));

        await factory.DrainAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(id)).StatusCode);
        Assert.Equal(0, factory.Count("SELECT COUNT(*) FROM Events WHERE Payload LIKE '%must-not-survive-the-delete%'"));
        Assert.Equal(0, factory.Count("SELECT COUNT(*) FROM Entities WHERE Payload LIKE '%must-not-survive-the-delete%'"));
    }

    // ---- validation happens before the acknowledgement -----------------------------------------------------

    [Fact]
    [Trait("req", "IN-04")]
    [Trait("req", "EV-13")]
    public async Task Events_that_can_never_be_applied_are_decided_on_arrival_and_the_rest_are_queued()
    {
        var good = Evt.Id();
        var bad = Evt.Id();
        using var ack = await factory.AcceptAsync(
            Evt.Publish(good, 1, 1),
            new { type = "publish", id = bad, version = 0, payload = new { }, timestamp = Evt.T(1) },
            new { type = "mystery", id = Evt.Id(), timestamp = Evt.T(1) });
        var batchId = CmsFactory.BatchId(ack);

        Assert.Equal(["queued", "rejected", "ignored_unsupported_type"], ack.Outcomes());
        Assert.Equal("invalid_version", ack.Reasons()[1]);
        Assert.Equal(1, ack.RootElement.GetProperty("queued").GetInt32());
        Assert.Equal(1, ack.RootElement.GetProperty("rejected").GetInt32());

        // All three are rows from the first transaction; the two decided ones are already final.
        Assert.Equal(3, Rows("Events", batchId));
        Assert.Equal(
            ["queued", "rejected", "ignored_unsupported_type"],
            factory.Sql($"SELECT Outcome FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE ORDER BY Sequence").Select(r => (string)r[0]!));
        Assert.Equal(["queued", "rejected", "ignored_unsupported_type"], await OutcomesAsync(batchId));

        await factory.DrainAsync();

        Assert.Equal(["applied", "rejected", "ignored_unsupported_type"], await OutcomesAsync(batchId));
        Assert.Equal(3, Rows("Events", batchId));
    }

    [Fact]
    [Trait("req", "IN-04")]
    public async Task A_batch_with_nothing_left_to_queue_is_answered_200_with_its_final_outcomes()
    {
        var response = await factory.Cms().PostAsync(
            new { type = "publish", id = Evt.Id(), version = 0, payload = new { }, timestamp = Evt.T(1) },
            new { type = "mystery", id = Evt.Id(), timestamp = Evt.T(1) });
        var body = await response.ReadJsonAsync();
        var batchId = CmsFactory.BatchId(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(["rejected", "ignored_unsupported_type"], body.Outcomes());

        // It is recorded all the same, already completed, so the ledger has every request.
        Assert.Equal(1, Rows("Batches", batchId));
        Assert.Equal(2, Rows("Events", batchId));
        Assert.Equal("completed", await factory.StatusNameAsync(batchId));
    }

    [Fact]
    [Trait("req", "IN-04")]
    public async Task An_empty_batch_is_a_completed_no_op_that_is_still_recorded()
    {
        var response = await factory.Cms().PostAsync();
        var body = await response.ReadJsonAsync();
        var batchId = CmsFactory.BatchId(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("received").GetInt32());
        Assert.Equal(1, Rows("Batches", batchId));
        Assert.Equal(0, Rows("Events", batchId));

        using var status = await factory.StatusAsync(batchId);
        Assert.Equal("completed", status.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, status.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    [Trait("req", "IN-06")]
    public async Task A_request_that_is_refused_as_a_whole_stores_nothing()
    {
        var before = factory.Count("SELECT COUNT(*) FROM Batches");

        var response = await factory.Cms().PostAsync("/cms/events", Evt.Raw("{ not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, factory.Count("SELECT COUNT(*) FROM Batches"));
    }

    // ---- following a batch ---------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "AS-06")]
    public async Task The_status_of_a_batch_goes_from_queued_to_completed_and_says_when()
    {
        var id = Evt.Id();
        var batchId = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(id, 1, 1)));

        using (var waiting = await factory.StatusAsync(batchId))
        {
            Assert.Equal("queued", waiting.RootElement.GetProperty("status").GetString());
            Assert.Equal(["queued"], waiting.Outcomes());
            Assert.True(waiting.RootElement.TryGetProperty("receivedAt", out _));
            Assert.False(waiting.RootElement.TryGetProperty("completedAt", out _));
        }

        factory.Time.Advance(TimeSpan.FromSeconds(42));
        await factory.DrainAsync();

        using var done = await factory.StatusAsync(batchId);
        Assert.Equal("completed", done.RootElement.GetProperty("status").GetString());
        Assert.Equal(["applied"], done.Outcomes());
        var received = done.RootElement.GetProperty("receivedAt").GetDateTime();
        var completed = done.RootElement.GetProperty("completedAt").GetDateTime();
        Assert.Equal(TimeSpan.FromSeconds(42), completed - received);
    }

    [Fact]
    [Trait("req", "AS-06")]
    public async Task The_status_never_gives_the_payload_back()
    {
        var batchId = CmsFactory.BatchId(
            await factory.AcceptAsync(Evt.Publish(Evt.Id(), 1, 1, new { title = "status-sentinel" })));

        var waiting = await (await factory.Cms().GetAsync($"/cms/batches/{batchId}")).Content.ReadAsStringAsync();
        await factory.DrainAsync();
        var done = await (await factory.Cms().GetAsync($"/cms/batches/{batchId}")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("status-sentinel", waiting);
        Assert.DoesNotContain("status-sentinel", done);
    }

    [Theory]
    [Trait("req", "AS-06")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    [InlineData("not-a-guid")]
    public async Task An_unknown_batch_is_404(string id)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Cms().GetAsync($"/cms/batches/{id}")).StatusCode);
    }

    // ---- idempotence and concurrency -----------------------------------------------------------------------

    [Fact]
    [Trait("req", "EV-03")]
    public async Task Sending_a_batch_twice_is_harmless_the_second_is_just_a_duplicate()
    {
        var id = Evt.Id();
        var first = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(id, 1, 1)));
        var second = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(id, 1, 1)));

        await factory.DrainAsync();

        Assert.Equal(["applied"], await OutcomesAsync(first));
        Assert.Equal(["ignored_duplicate"], await OutcomesAsync(second));
        Assert.Equal(1, factory.Count($"SELECT COUNT(*) FROM Entities WHERE Id = '{id}'"));
    }

    [Fact]
    [Trait("req", "AS-05")]
    public async Task Many_senders_at_once_lose_nothing()
    {
        var ids = Enumerable.Range(0, 40).Select(_ => Evt.Id()).ToArray();

        var acknowledgements = await Task.WhenAll(ids.Select(async id =>
        {
            var response = await factory.Cms().PostAsync(Evt.Publish(id, 1, 1));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return CmsFactory.BatchId(await response.ReadJsonAsync());
        }));
        await factory.DrainAsync();

        Assert.Equal(ids.Length, acknowledgements.Distinct().Count());
        foreach (var id in ids)
        {
            Assert.Equal(HttpStatusCode.OK, (await Admin.GetEntityAsync(id)).StatusCode);
        }

        foreach (var batchId in acknowledgements)
        {
            Assert.Equal(1, factory.Count($"SELECT COUNT(*) FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE AND Outcome = 'applied'"));
        }
    }

    // ---- order between batches -----------------------------------------------------------------------------

    [Fact]
    [Trait("req", "AS-03")]
    public async Task A_batch_with_a_takedown_is_applied_before_older_batches_that_only_publish()
    {
        await factory.DrainAsync();
        var published = Evt.Id();
        var takenDown = Evt.Id();
        await factory.AcceptAsync(Evt.Publish(published, 1, 1));
        await factory.AcceptAsync(Evt.UnPublish(takenDown, 2, 2));

        Assert.Equal(Lateral.Cms.Ingestion.ProcessStep.Applied, await factory.StepAsync());

        // The takedown, accepted second, went first.
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetEntityAsync(takenDown)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(published)).StatusCode);

        await factory.DrainAsync();
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetEntityAsync(published)).StatusCode);
    }

    [Fact]
    [Trait("req", "AS-03")]
    public async Task Batches_without_a_takedown_are_applied_oldest_first()
    {
        await factory.DrainAsync();
        var first = Evt.Id();
        var second = Evt.Id();
        await factory.AcceptAsync(Evt.Publish(first, 1, 1));
        await factory.AcceptAsync(Evt.Publish(second, 1, 1));

        await factory.StepAsync();

        Assert.Equal(HttpStatusCode.OK, (await Admin.GetEntityAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(second)).StatusCode);
        await factory.DrainAsync();
    }

    [Fact]
    [Trait("req", "EV-12")]
    public async Task Reordering_batches_cannot_change_the_outcome_a_late_publish_cannot_undo_an_earlier_takedown()
    {
        await factory.DrainAsync();
        var id = Evt.Id();
        var older = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(id, 1, 1)));
        var takedown = CmsFactory.BatchId(await factory.AcceptAsync(Evt.UnPublish(id, 2, 2)));

        await factory.DrainAsync();

        // The takedown was applied first; the older publish then lost, exactly as it would have in order.
        Assert.Equal(["applied"], await OutcomesAsync(takedown));
        Assert.Equal(["ignored_stale"], await OutcomesAsync(older));
        using var entity = await (await Admin.GetEntityAsync(id)).ReadJsonAsync();
        Assert.Equal(2, entity.RootElement.GetProperty("version").GetInt32());
        Assert.False(entity.RootElement.GetProperty("status").GetProperty("isPublished").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await factory.User().GetEntityAsync(id)).StatusCode);
    }

    [Fact]
    [Trait("req", "EV-12")]
    public async Task Random_streams_split_into_batches_and_applied_in_the_queues_own_order_end_where_the_oracle_says()
    {
        await factory.DrainAsync();
        var rng = new Random(31337);
        var runId = Guid.NewGuid().ToString("N")[..8];
        var expected = new Dictionary<string, ReducerPropertyTests.Snap>();
        var chunks = new List<object[]>();

        for (var n = 0; n < 60; n++)
        {
            var id = $"part-{runId}-{n}";
            var timeline = ReducerPropertyTests.Timeline(rng).Select(e => e with { EntityId = id }).ToList();
            expected[id] = ReducerPropertyTests.Oracle(timeline);
            var shuffled = timeline.OrderBy(_ => rng.Next()).ToList();
            for (var i = 0; i < shuffled.Count;)
            {
                var size = rng.Next(1, 4);
                chunks.Add([.. shuffled.Skip(i).Take(size).Select(ToWire)]);
                i += size;
            }
        }

        // Accept all, in a scrambled order, then let the queue apply them (takedown batches first, then oldest).
        foreach (var chunk in chunks.OrderBy(_ => rng.Next()))
        {
            await factory.AcceptAsync(chunk);
        }

        await factory.DrainAsync();

        foreach (var (id, snap) in expected)
        {
            var entity = factory.Sql($"SELECT Version, IsPublished, Payload FROM Entities WHERE Id = '{id}'");
            var tombstone = factory.Sql($"SELECT COUNT(*) FROM Tombstones WHERE Id = '{id}'");
            if (snap.Exists)
            {
                var row = Assert.Single(entity);
                Assert.Equal(snap.Version, Convert.ToInt64(row[0]));
                Assert.Equal(snap.Published, Convert.ToInt64(row[1]) == 1);
                Assert.Equal(snap.Payload, (string)row[2]!);
            }
            else
            {
                Assert.Empty(entity);
            }

            Assert.Equal(snap.Tombstone is not null, Convert.ToInt64(tombstone[0][0]) == 1);
        }
    }

    private static object ToWire(CmsEvent e) =>
        e.Type switch
        {
            CmsEventType.Delete => Evt.Delete(e.EntityId, (int)(e.Timestamp - new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes),
            CmsEventType.Publish => Evt.Publish(e.EntityId, e.Version!.Value, Minutes(e), JsonDocument.Parse(e.Payload!).RootElement),
            _ => Evt.UnPublish(e.EntityId, e.Version!.Value, Minutes(e), JsonDocument.Parse(e.Payload!).RootElement),
        };

    private static int Minutes(CmsEvent e) => (int)(e.Timestamp - new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes;

    // ---- no leaks ------------------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "SE-07")]
    [Trait("req", "OB-01")]
    public async Task Accepting_and_applying_log_the_events_but_never_their_content_or_credentials()
    {
        var id = Evt.Id();
        await factory.AcceptAsync(Evt.Publish(id, 1, 1, new { title = "async-log-sentinel" }));
        await factory.DrainAsync();

        var mine = factory.Logs.Entries.Where(e => e.Message.Contains(id)).ToList();
        Assert.Contains(mine, e => e.Message.Contains("Applied"));

        var everything = string.Join('\n', factory.Logs.Entries.Select(e => e.Message + e.Exception));
        Assert.DoesNotContain("async-log-sentinel", everything);
        Assert.DoesNotContain(CmsFactory.CmsPassword, everything);
    }

    [Fact]
    [Trait("req", "AS-09")]
    [Trait("req", "ST-05")]
    public void The_events_table_is_the_only_place_a_queued_payload_can_live()
    {
        // A new table that stored payloads would be a new place for a deleted entity's content to survive.
        _ = factory.Services;
        var tables = factory.Sql("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")
            .Select(r => (string)r[0]!).Where(n => !n.StartsWith("__", StringComparison.Ordinal) && !n.StartsWith("sqlite_", StringComparison.Ordinal));

        Assert.Equal(["Batches", "Entities", "Events", "Tombstones"], tables);
    }
}
