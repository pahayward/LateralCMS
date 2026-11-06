using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Integrity;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Tests.Api;

/// <summary>The payload checksum from end to end: computed, stored, returned, and re-verifiable.</summary>
[Trait("req", "IN-07")]
public class ChecksumEndpointTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private static readonly HashPayloadChecksum Checksums = new(Options.Create(new IngestionOptions()));

    private HttpClient Cms => factory.Cms();

    private static string Sha256Of(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? ChecksumOf(JsonDocument ingestResult, int index = 0) =>
        ingestResult.RootElement.GetProperty("results")[index].TryGetProperty("checksum", out var value) ? value.GetString() : null;

    [Fact]
    public async Task Publishing_returns_the_checksum_of_the_payload_that_was_stored()
    {
        var id = Evt.Id();

        using var result = await factory.SendAsync(Evt.Publish(id, 1, 1, new { title = "Hello", n = 1 }));

        // For plain ASCII the canonical text is just the compact JSON, so a sender can reproduce this itself.
        Assert.Equal(Sha256Of("""{"title":"Hello","n":1}"""), ChecksumOf(result));
        Assert.Equal(["applied"], result.Outcomes());
    }

    [Fact]
    public async Task The_same_checksum_is_served_by_the_api_to_users_and_admins_and_in_listings()
    {
        var id = Evt.Id();
        using var result = await factory.SendAsync(Evt.Publish(id, 1, 1, new { title = "Hello" }));
        var sent = ChecksumOf(result);

        using var asUser = await (await factory.User().GetEntityAsync(id)).ReadJsonAsync();
        using var asAdmin = await (await factory.Admin().GetEntityAsync(id)).ReadJsonAsync();
        using var list = await (await factory.User().GetAsync("/api/entities?limit=100")).ReadJsonAsync();

        Assert.Equal(sent, asUser.RootElement.GetProperty("checksum").GetString());
        Assert.Equal(sent, asAdmin.RootElement.GetProperty("checksum").GetString());
        var listed = list.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);
        Assert.Equal(sent, listed.GetProperty("checksum").GetString());
    }

    [Fact]
    public async Task A_consumer_can_verify_the_payload_it_was_served_against_the_checksum()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1, new { html = "<b>é & ü</b>", list = new[] { 1, 2, 3 } }));

        var response = await factory.User().GetEntityAsync(id);
        using var entity = await response.ReadJsonAsync();

        // The payload is served exactly as stored, so its raw text can be checked as received.
        var rawPayload = entity.RootElement.GetProperty("payload").GetRawText();
        var checksum = entity.RootElement.GetProperty("checksum").GetString()!;
        Assert.True(Checksums.Verify(checksum, Encoding.UTF8.GetBytes(rawPayload)));
        Assert.False(Checksums.Verify(checksum, Encoding.UTF8.GetBytes(rawPayload.Replace("3", "4"))));
    }

    [Fact]
    public async Task Every_stored_payload_matches_its_stored_checksum()
    {
        var ids = new[] { Evt.Id(), Evt.Id(), Evt.Id() };
        await factory.SendAsync(
            Evt.Publish(ids[0], 1, 1, new { a = "plain" }),
            Evt.Publish(ids[1], 1, 1, new { a = "non-ascii é ü 日本語 <tag> & 'quote'" }),
            Evt.UnPublish(ids[2], 3, 1, new { draft = true }));
        await factory.SendAsync(Evt.Publish(ids[0], 2, 2, new { a = "changed" }));

        var rows = factory.Sql("SELECT Id, Payload, PayloadChecksum FROM Entities");

        Assert.True(rows.Count >= 3);
        foreach (var row in rows)
        {
            var checksum = (string)row[2]!;
            Assert.True(
                Checksums.Verify(checksum, Encoding.UTF8.GetBytes((string)row[1]!)),
                $"entity {row[0]} stored payload does not match its stored checksum");
        }
    }

    [Fact]
    public async Task An_update_changes_the_checksum_and_a_replay_returns_the_same_one()
    {
        var id = Evt.Id();
        using var first = await factory.SendAsync(Evt.Publish(id, 1, 1, new { title = "one" }));
        using var second = await factory.SendAsync(Evt.Publish(id, 2, 2, new { title = "two" }));
        using var replay = await factory.SendAsync(Evt.Publish(id, 2, 2, new { title = "two" }));

        Assert.NotEqual(ChecksumOf(first), ChecksumOf(second));
        Assert.Equal(["ignored_duplicate"], replay.Outcomes());
        Assert.Equal(ChecksumOf(second), ChecksumOf(replay));
    }

    [Fact]
    public async Task A_stale_event_reports_the_checksum_of_what_it_carried_not_of_what_is_stored()
    {
        var id = Evt.Id();
        using var current = await factory.SendAsync(Evt.Publish(id, 3, 3, new { title = "v3" }));

        using var stale = await factory.SendAsync(Evt.Publish(id, 2, 2, new { title = "v2" }));

        Assert.Equal(["ignored_stale"], stale.Outcomes());
        Assert.Equal(Sha256Of("""{"title":"v2"}"""), ChecksumOf(stale));
        using var stored = await (await factory.User().GetEntityAsync(id)).ReadJsonAsync();
        Assert.Equal(ChecksumOf(current), stored.RootElement.GetProperty("checksum").GetString());
    }

    [Fact]
    public async Task Unpublish_events_are_checksummed_and_stored_too()
    {
        var id = Evt.Id();

        using var result = await factory.SendAsync(Evt.UnPublish(id, 2, 1, new { title = "draft" }));

        Assert.Equal(Sha256Of("""{"title":"draft"}"""), ChecksumOf(result));
        using var stored = await (await factory.Admin().GetEntityAsync(id)).ReadJsonAsync();
        Assert.Equal(ChecksumOf(result), stored.RootElement.GetProperty("checksum").GetString());
    }

    [Fact]
    public async Task Deletes_and_rejected_events_have_no_checksum_in_the_response()
    {
        var id = Evt.Id();

        using var result = await factory.SendAsync(
            Evt.Delete(id, 1),
            new { type = "publish", id, version = 0, payload = new { }, timestamp = Evt.T(1) });

        Assert.Null(ChecksumOf(result, 0));
        Assert.Null(ChecksumOf(result, 1));
    }

    [Fact]
    public async Task The_ledger_records_the_same_checksum_the_sender_was_given()
    {
        var id = Evt.Id();
        var response = await Cms.PostAsync(Evt.Publish(id, 1, 1, new { title = "ledger" }));
        using var body = await response.ReadJsonAsync();

        var rows = factory.Sql($"SELECT PayloadChecksum FROM Events WHERE EntityId = '{id}'");

        Assert.Equal(ChecksumOf(body), (string?)Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Applied_events_are_logged_with_their_checksum_but_never_their_payload()
    {
        var id = Evt.Id();
        using var result = await factory.SendAsync(Evt.Publish(id, 1, 1, new { title = "checksum-log-sentinel" }));

        var entry = factory.Logs.Entries.Single(e => e.Message.Contains(id) && e.Message.Contains("Applied"));

        Assert.Contains(ChecksumOf(result)!, entry.Message);
        Assert.DoesNotContain("checksum-log-sentinel", entry.Message);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    [Fact]
    public async Task A_content_conflict_logs_at_warning_for_winner_and_loser_whichever_arrived_first()
    {
        var first = Evt.Id();
        var second = Evt.Id();
        var plainApply = Evt.Id();

        await factory.SendAsync(Evt.Publish(first, 1, 1, new { title = "a" }), Evt.Publish(first, 1, 1, new { title = "b" }));
        await factory.SendAsync(Evt.Publish(second, 1, 1, new { title = "b" }), Evt.Publish(second, 1, 1, new { title = "a" }));
        await factory.SendAsync(Evt.Publish(plainApply, 1, 1, new { title = "a" }));

        // 'a' then 'b': the winner is the second event (applied). 'b' then 'a': the loser is the second event
        // (ignored_stale). Either way exactly one line reports the conflict, and it is a Warning.
        foreach (var id in new[] { first, second })
        {
            var line = factory.Logs.Entries.Single(e => e.Message.Contains(id) && e.Message.Contains("same_version_different_content"));
            Assert.Equal(LogLevel.Warning, line.Level);
        }
        Assert.Contains("Applied", factory.Logs.Entries.Single(e => e.Message.Contains(first) && e.Message.Contains("same_version_different_content")).Message);
        Assert.Contains("IgnoredStale", factory.Logs.Entries.Single(e => e.Message.Contains(second) && e.Message.Contains("same_version_different_content")).Message);

        // An ordinary applied event is still routine.
        Assert.Equal(LogLevel.Information, factory.Logs.Entries.Single(e => e.Message.Contains(plainApply) && e.Message.Contains("Applied")).Level);
    }

    [Fact]
    public async Task Contradicting_content_at_one_version_and_instant_resolves_the_same_way_in_either_order()
    {
        var first = Evt.Id();
        var second = Evt.Id();
        var a = new { title = "a" };
        var b = new { title = "b" };

        using var resultAb = await factory.SendAsync(Evt.Publish(first, 1, 1, a), Evt.Publish(first, 1, 1, b));
        using var resultBa = await factory.SendAsync(Evt.Publish(second, 1, 1, b), Evt.Publish(second, 1, 1, a));

        // 'b' sorts after 'a', so it wins whichever arrived first, and the conflict is reported.
        Assert.Equal(["applied", "applied"], resultAb.Outcomes());
        Assert.Equal(["", "same_version_different_content"], resultAb.Reasons());
        Assert.Equal(["applied", "ignored_stale"], resultBa.Outcomes());
        Assert.Equal(["", "same_version_different_content"], resultBa.Reasons());
        foreach (var id in new[] { first, second })
        {
            using var stored = await (await factory.User().GetEntityAsync(id)).ReadJsonAsync();
            Assert.Equal("b", stored.RootElement.GetProperty("payload").GetProperty("title").GetString());
            Assert.Equal(Sha256Of("""{"title":"b"}"""), stored.RootElement.GetProperty("checksum").GetString());
        }
    }
}

/// <summary>MD5 is available by configuration, for senders that expect it.</summary>
public class Md5ChecksumTests : IClassFixture<Md5ChecksumTests.Md5Factory>
{
    public sealed class Md5Factory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
            [new("Ingestion:ChecksumAlgorithm", "md5")];
    }

    private readonly Md5Factory _factory;

    public Md5ChecksumTests(Md5Factory factory) => _factory = factory;

    [Fact]
    public async Task The_configured_md5_checksum_is_returned_stored_and_served()
    {
        var id = Evt.Id();

        using var result = await _factory.SendAsync(Evt.Publish(id, 1, 1, new { title = "Hello" }));

        var expected = "md5:" + Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("""{"title":"Hello"}""")));
        var checksum = result.RootElement.GetProperty("results")[0].GetProperty("checksum").GetString();
        Assert.Equal(expected, checksum);
        Assert.Equal(expected, (string)_factory.Sql($"SELECT PayloadChecksum FROM Entities WHERE Id = '{id}'")[0][0]!);
        using var served = await (await _factory.User().GetEntityAsync(id)).ReadJsonAsync();
        Assert.Equal(expected, served.RootElement.GetProperty("checksum").GetString());
    }
}
