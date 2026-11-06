using System.Net;
using System.Text.Json;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Lateral.Cms.Tests.Api;

/// <summary>How events are processed through POST /cms/events, observed through the public API.</summary>
[Trait("req", "IN-04")]
[Trait("req", "IN-08")]
[Trait("req", "IN-01")]
[Trait("req", "EV-01")]
[Trait("req", "EV-02")]
[Trait("req", "EV-04")]
[Trait("req", "EV-05")]
[Trait("req", "EV-06")]
[Trait("req", "EV-07")]
[Trait("req", "EV-13")]
public class IngestionEndpointTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private HttpClient Cms => factory.Cms();

    private HttpClient User => factory.User();

    private HttpClient Admin => factory.Admin();

    private static async Task<JsonDocument> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    // ---- publish / update -------------------------------------------------------------------------------

    [Fact]
    public async Task A_published_entity_becomes_visible_to_users()
    {
        var id = Evt.Id();

        using var result = await factory.SendAsync(Evt.Publish(id, 1, 1, new { title = "Hello" }));

        Assert.Equal(["applied"], result.Outcomes());
        using var entity = await Read(await User.GetEntityAsync(id));
        Assert.Equal(id, entity.RootElement.GetProperty("id").GetString());
        Assert.Equal(1, entity.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("Hello", entity.RootElement.GetProperty("payload").GetProperty("title").GetString());
    }

    [Fact]
    public async Task An_update_replaces_the_payload_and_tracks_the_latest_version()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1, new { title = "old", obsolete = true }));

        using var result = await factory.SendAsync(Evt.Publish(id, 2, 2, new { title = "new" }));

        Assert.Equal(["applied"], result.Outcomes());
        using var entity = await Read(await User.GetEntityAsync(id));
        Assert.Equal(2, entity.RootElement.GetProperty("version").GetInt32());
        var payload = entity.RootElement.GetProperty("payload");
        Assert.Equal("new", payload.GetProperty("title").GetString());
        Assert.False(payload.TryGetProperty("obsolete", out _), "payload must be replaced, not merged");
    }

    [Fact]
    public async Task Skipped_versions_are_fine_because_unpublished_edits_are_never_sent()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        using var result = await factory.SendAsync(Evt.Publish(id, 4, 2));

        Assert.Equal(["applied"], result.Outcomes());
        using var entity = await Read(await User.GetEntityAsync(id));
        Assert.Equal(4, entity.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Several_versions_of_one_entity_in_one_batch_are_folded_in_order()
    {
        var id = Evt.Id();

        using var result = await factory.SendAsync(Evt.Publish(id, 1, 1), Evt.Publish(id, 2, 2), Evt.Publish(id, 3, 3));

        Assert.Equal(["applied", "applied", "applied"], result.Outcomes());
        using var entity = await Read(await User.GetEntityAsync(id));
        Assert.Equal(3, entity.RootElement.GetProperty("version").GetInt32());
    }

    // ---- out of order and replays -----------------------------------------------------------------------

    [Fact]
    public async Task A_late_older_version_does_not_overwrite_a_newer_one()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 3, 3, new { title = "v3" }));

        using var result = await factory.SendAsync(Evt.Publish(id, 2, 2, new { title = "v2" }));

        Assert.Equal(["ignored_stale"], result.Outcomes());
        Assert.Equal([Lateral.Cms.Domain.ReasonCodes.OlderVersion], result.Reasons());
        using var entity = await Read(await User.GetEntityAsync(id));
        Assert.Equal("v3", entity.RootElement.GetProperty("payload").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Events_arriving_in_reverse_order_give_the_same_result_as_in_order()
    {
        var inOrder = Evt.Id();
        var reversed = Evt.Id();

        await factory.SendAsync(Evt.Publish(inOrder, 1, 1), Evt.Publish(inOrder, 2, 2), Evt.UnPublish(inOrder, 2, 3));
        await factory.SendAsync(Evt.UnPublish(reversed, 2, 3), Evt.Publish(reversed, 2, 2), Evt.Publish(reversed, 1, 1));

        using var a = await Read(await Admin.GetEntityAsync(inOrder));
        using var b = await Read(await Admin.GetEntityAsync(reversed));
        foreach (var entity in new[] { a, b })
        {
            Assert.Equal(2, entity.RootElement.GetProperty("version").GetInt32());
            Assert.False(entity.RootElement.GetProperty("status").GetProperty("isPublished").GetBoolean());
        }
    }

    [Fact]
    public async Task Replaying_a_batch_is_harmless()
    {
        var id = Evt.Id();
        object[] batch = [Evt.Publish(id, 1, 1), Evt.Publish(id, 2, 2)];
        await factory.SendAsync(batch);

        using var replay = await factory.SendAsync(batch);

        // v1 is older than the stored v2; v2 is exactly what is stored.
        Assert.Equal(["ignored_stale", "ignored_duplicate"], replay.Outcomes());
        using var entity = await Read(await User.GetEntityAsync(id));
        Assert.Equal(2, entity.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Concurrent_batches_for_one_entity_converge_on_the_newest_version()
    {
        var id = Evt.Id();
        var versions = Enumerable.Range(1, 12).OrderBy(_ => Guid.NewGuid()).ToList();

        var responses = await Task.WhenAll(versions.Select(v => factory.Cms().PostAsync(Evt.Publish(id, v, v))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        await factory.DrainAsync();
        using var entity = await Read(await User.GetEntityAsync(id));
        Assert.Equal(12, entity.RootElement.GetProperty("version").GetInt32());
    }

    // ---- unpublish --------------------------------------------------------------------------------------

    [Fact]
    public async Task Unpublish_hides_the_entity_from_users_but_keeps_it_for_admins()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1, new { title = "keep" }));

        using var result = await factory.SendAsync(Evt.UnPublish(id, 1, 2, new { title = "keep" }));

        Assert.Equal(["applied"], result.Outcomes());
        Assert.Equal(HttpStatusCode.NotFound, (await User.GetEntityAsync(id)).StatusCode);
        using var entity = await Read(await Admin.GetEntityAsync(id));
        Assert.False(entity.RootElement.GetProperty("status").GetProperty("isPublished").GetBoolean());
        Assert.Equal("keep", entity.RootElement.GetProperty("payload").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unpublish_of_a_version_we_never_saw_published_still_stores_the_entity()
    {
        // Version 1 was created, edited to 2, and unpublished without ever being published. The unpublish
        // event is the first we hear of it and carries the full entity.
        var id = Evt.Id();

        using var result = await factory.SendAsync(Evt.UnPublish(id, 2, 1, new { title = "draft two" }));

        Assert.Equal(["applied"], result.Outcomes());
        Assert.Equal(HttpStatusCode.NotFound, (await User.GetEntityAsync(id)).StatusCode);
        using var entity = await Read(await Admin.GetEntityAsync(id));
        Assert.Equal(2, entity.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("draft two", entity.RootElement.GetProperty("payload").GetProperty("title").GetString());
        Assert.False(entity.RootElement.GetProperty("status").GetProperty("isPublished").GetBoolean());
    }

    [Fact]
    public async Task Unpublish_of_a_newer_version_than_stored_updates_the_stored_data_to_that_version()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1, new { title = "published v1" }));

        await factory.SendAsync(Evt.UnPublish(id, 3, 2, new { title = "draft v3" }));

        using var entity = await Read(await Admin.GetEntityAsync(id));
        Assert.Equal(3, entity.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("draft v3", entity.RootElement.GetProperty("payload").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Publishing_again_after_an_unpublish_makes_the_entity_visible_again()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1), Evt.UnPublish(id, 1, 2));
        Assert.Equal(HttpStatusCode.NotFound, (await User.GetEntityAsync(id)).StatusCode);

        await factory.SendAsync(Evt.Publish(id, 1, 3));

        Assert.Equal(HttpStatusCode.OK, (await User.GetEntityAsync(id)).StatusCode);
    }

    // ---- delete -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_removes_the_entity_for_everyone_including_admins()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        using var result = await factory.SendAsync(Evt.Delete(id, 2));

        Assert.Equal(["applied"], result.Outcomes());
        Assert.Equal(HttpStatusCode.NotFound, (await User.GetEntityAsync(id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(id)).StatusCode);
        Assert.Empty(factory.Sql($"SELECT 1 FROM Entities WHERE Id = '{id}'"));
    }

    [Fact]
    public async Task A_delete_keeps_no_entity_data_only_a_tombstone()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1, new { secret = "classified" }), Evt.Delete(id, 2));

        var tombstones = factory.Sql($"SELECT Id FROM Tombstones WHERE Id = '{id}'");
        var anywhere = factory.Sql($"SELECT 1 FROM Entities WHERE Payload LIKE '%classified%'");

        Assert.Single(tombstones);
        Assert.Empty(anywhere);
    }

    [Fact]
    public async Task A_late_publish_cannot_resurrect_a_deleted_entity()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Delete(id, 5));

        using var result = await factory.SendAsync(Evt.Publish(id, 1, 3));

        Assert.Equal(["ignored_stale"], result.Outcomes());
        Assert.Equal([Lateral.Cms.Domain.ReasonCodes.SupersededByDelete], result.Reasons());
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(id)).StatusCode);
    }

    [Fact]
    public async Task A_publish_after_the_delete_creates_the_entity_again()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1), Evt.Delete(id, 2));

        using var result = await factory.SendAsync(Evt.Publish(id, 2, 3));

        Assert.Equal(["applied"], result.Outcomes());
        Assert.Equal(HttpStatusCode.OK, (await User.GetEntityAsync(id)).StatusCode);
    }

    [Fact]
    public async Task Deleting_something_unknown_is_accepted()
    {
        using var result = await factory.SendAsync(Evt.Delete(Evt.Id(), 1));

        Assert.Equal(["applied"], result.Outcomes());
        Assert.Equal([Lateral.Cms.Domain.ReasonCodes.TombstoneOnly], result.Reasons());
    }

    // ---- the admin override -----------------------------------------------------------------------------

    [Fact]
    public async Task A_cms_publish_cannot_undo_an_admin_disable()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));
        (await Admin.PostAsync($"/api/entities/{id}/disable", null)).EnsureSuccessStatusCode();

        await factory.SendAsync(Evt.Publish(id, 2, 2, new { title = "updated by cms" }));

        Assert.Equal(HttpStatusCode.NotFound, (await User.GetEntityAsync(id)).StatusCode);
        using var entity = await Read(await Admin.GetEntityAsync(id));
        Assert.Equal(2, entity.RootElement.GetProperty("version").GetInt32());
        Assert.True(entity.RootElement.GetProperty("status").GetProperty("isDisabledByAdmin").GetBoolean());
    }

    // ---- batches: partial failure ----------------------------------------------------------------------

    [Fact]
    public async Task Invalid_events_are_reported_while_valid_ones_in_the_same_batch_are_applied()
    {
        var good = Evt.Id();
        var bad = Evt.Id();

        using var result = await factory.SendAsync(
            Evt.Publish(good, 1, 1),
            new { type = "publish", id = bad, version = 0, payload = new { }, timestamp = Evt.T(1) },
            new { type = "publish", id = bad, version = 1, timestamp = Evt.T(1) });

        Assert.Equal(["applied", "rejected", "rejected"], result.Outcomes());
        Assert.Equal(["", "invalid_version", "missing_payload"], result.Reasons());
        var root = result.RootElement;
        Assert.Equal(3, root.GetProperty("received").GetInt32());
        Assert.Equal(1, root.GetProperty("applied").GetInt32());
        Assert.Equal(2, root.GetProperty("rejected").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await User.GetEntityAsync(good)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(bad)).StatusCode);
    }

    [Fact]
    public async Task Unknown_event_types_are_ignored_and_do_not_fail_the_batch()
    {
        var id = Evt.Id();

        using var result = await factory.SendAsync(
            new { type = "archive", id, timestamp = Evt.T(1) },
            Evt.Publish(id, 1, 2));

        Assert.Equal(["ignored_unsupported_type", "applied"], result.Outcomes());
    }

    [Fact]
    public async Task Each_result_says_which_event_it_belongs_to()
    {
        var id = Evt.Id();

        using var result = await factory.SendAsync(Evt.Publish(id, 1, 1));

        var first = result.RootElement.GetProperty("results")[0];
        Assert.Equal(0, first.GetProperty("index").GetInt32());
        Assert.Equal(id, first.GetProperty("id").GetString());
        Assert.Equal("publish", first.GetProperty("type").GetString());
        Assert.Equal(1, first.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task An_empty_batch_is_accepted()
    {
        using var result = await factory.SendAsync();

        Assert.Equal(0, result.RootElement.GetProperty("received").GetInt32());
    }

    [Fact]
    public async Task Ids_with_awkward_characters_round_trip()
    {
        var id = $"news 2024 spring & summer? #é{Guid.NewGuid():N}";
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        using var entity = await Read(await User.GetEntityAsync(id));

        Assert.Equal(id, entity.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Payloads_are_returned_as_the_same_data_that_was_sent_with_html_neutralised_in_transit()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1, new { html = "<img src=x onerror=alert(1)>", n = 1.5, nested = new { list = new[] { 1, 2 } } }));

        var response = await User.GetEntityAsync(id);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<img", raw);
        using var entity = JsonDocument.Parse(raw);
        var payload = entity.RootElement.GetProperty("payload");
        Assert.Equal("<img src=x onerror=alert(1)>", payload.GetProperty("html").GetString());
        Assert.Equal(1.5, payload.GetProperty("n").GetDouble());
        Assert.Equal(2, payload.GetProperty("nested").GetProperty("list")[1].GetInt32());
    }

    // ---- request-level failures ------------------------------------------------------------------------

    [Fact]
    public async Task A_body_that_is_not_json_gets_400()
    {
        var response = await Cms.PostAsync("/cms/events", Evt.Raw("{ not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await response.ReadJsonAsync();
        Assert.Equal("invalid_json", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_body_that_is_not_an_array_gets_400()
    {
        var response = await Cms.PostAsync("/cms/events", Evt.Raw("""{"type":"publish"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await response.ReadJsonAsync();
        Assert.Equal("invalid_batch", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_sample_in_the_brief_with_typographic_quotes_is_not_json_and_is_refused()
    {
        var response = await Cms.PostAsync("/cms/events", Evt.Raw(
            "[{ \"type\": \"delete\", \"id\": \"Y\", \"timestamp”: ”2024-01-01T00:00:00Z” }]"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_the_batch_id_for_correlation()
    {
        var id = Evt.Id();

        var response = await Cms.PostAsync(Evt.Publish(id, 1, 1));

        var header = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
        using var body = await response.ReadJsonAsync();
        Assert.Equal(header, body.RootElement.GetProperty("batchId").GetString());
    }

    // ---- ledger -----------------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "OB-03")]
    public async Task Every_event_leaves_a_ledger_row_whatever_its_outcome_and_never_the_payload()
    {
        var id = Evt.Id();
        var payload = new { title = "ledger-sentinel-value" };
        var response = await Cms.PostAsync(
            Evt.Publish(id, 1, 1, payload),
            Evt.Publish(id, 1, 1, payload),
            Evt.Publish(id, 0, 1),
            new { type = "mystery", id, timestamp = Evt.T(1) });
        using var body = await response.ReadJsonAsync();
        var batch = body.RootElement.GetProperty("batchId").GetString();
        await factory.DrainAsync();

        var rows = factory.Sql($"SELECT Sequence, Outcome, Reason, PayloadChecksum FROM Events WHERE BatchId = '{batch!.ToUpperInvariant()}' ORDER BY Sequence");

        Assert.Equal(
            ["applied", "ignored_duplicate", "rejected", "ignored_unsupported_type"],
            rows.Select(r => (string)r[1]!).ToArray());
        Assert.Equal("invalid_version", rows[2][2]);
        Assert.StartsWith("sha256:", (string)rows[0][3]!);
        Assert.Equal("sha256:".Length + 64, ((string)rows[0][3]!).Length);
        Assert.Null(rows[2][3]);

        // Every event is decided, so none still holds its content: only the checksum remains.
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM Events WHERE BatchId = '{batch.ToUpperInvariant()}' AND Payload IS NOT NULL"));
    }

    // ---- observability ----------------------------------------------------------------------------------

    [Fact]
    public async Task Processed_and_failing_events_are_logged_without_leaking_payloads_or_credentials()
    {
        var id = Evt.Id();
        await factory.SendAsync(
            Evt.Publish(id, 1, 1, new { title = "log-sentinel-payload-value" }),
            new { type = "publish", id, version = -1, payload = new { title = "log-sentinel-payload-value" }, timestamp = Evt.T(1) });

        var mine = factory.Logs.Entries.Where(e => e.Message.Contains(id)).ToList();

        Assert.Contains(mine, e => e.Level == LogLevel.Information && e.Message.Contains("Applied"));
        Assert.Contains(mine, e => e.Level == LogLevel.Warning && e.Message.Contains("Rejected") && e.Message.Contains("invalid_version"));

        var everything = string.Join('\n', factory.Logs.Entries.Select(e => e.Message + e.Exception));
        Assert.DoesNotContain("log-sentinel-payload-value", everything);
        Assert.DoesNotContain(CmsFactory.CmsPassword, everything);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{CmsFactory.CmsUser}:{CmsFactory.CmsPassword}")), everything);
    }

    // ---- atomicity --------------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "AS-01")]
    public async Task When_storing_a_batch_fails_nothing_from_it_is_kept_and_the_sender_is_told_to_retry()
    {
        var good = Evt.Id();
        var poison = "poison-" + Guid.NewGuid().ToString("N");

        // A trigger makes the ledger insert for one specific id fail, as a stand-in for any storage fault.
        factory.Sql($"CREATE TRIGGER \"fail_{poison}\" BEFORE INSERT ON Events WHEN NEW.EntityId = '{poison}' BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;");
        try
        {
            var response = await Cms.PostAsync(Evt.Publish(good, 1, 1), Evt.Publish(poison, 1, 1));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
            // The healthy event in the same batch was rolled back with it.
            Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(good)).StatusCode);
            Assert.Contains(factory.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("could not be stored"));
        }
        finally
        {
            factory.Sql($"DROP TRIGGER \"fail_{poison}\"");
        }

        // With the fault gone, the same batch simply works: the retry the sender was asked to make.
        using var retry = await factory.SendAsync(Evt.Publish(good, 1, 1), Evt.Publish(poison, 1, 1));
        Assert.Equal(["applied", "applied"], retry.Outcomes());
    }
}
