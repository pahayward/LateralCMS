using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lateral.Cms.Domain;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Integrity;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Tests.Ingestion;

/// <summary>The ingestion constraints: what is accepted, normalised and rejected, and why.</summary>
[Trait("req", "IN-03")]
[Trait("req", "IN-05")]
[Trait("req", "SE-09")]
[Trait("req", "EV-11")]
public class EventParserTests
{
    private static readonly DateTime Now = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_version_is_a_64_bit_integer_so_a_value_beyond_the_32_bit_range_is_valid()
    {
        var parsed = Parse("""{"type":"publish","id":"a","version":9223372036854775807,"payload":{},"timestamp":"2024-01-01T00:00:00Z"}""");

        Assert.NotNull(parsed.Event);
        Assert.Equal(long.MaxValue, parsed.Event.Version);
    }

    private static ParsedEvent Parse(string json, IngestionOptions? options = null)
    {
        options ??= new IngestionOptions();
        using var document = JsonDocument.Parse(json);
        return EventParser.Parse(
            document.RootElement, 7, Now, options, new HashPayloadChecksum(Options.Create(options)));
    }

    private static string Event(string? type = "\"publish\"", string? id = "\"a\"", string? version = "1",
        string? payload = "{\"k\":1}", string? timestamp = "\"2024-01-01T00:00:00Z\"")
    {
        var fields = new List<string>();
        if (type is not null) fields.Add($"\"type\":{type}");
        if (id is not null) fields.Add($"\"id\":{id}");
        if (version is not null) fields.Add($"\"version\":{version}");
        if (payload is not null) fields.Add($"\"payload\":{payload}");
        if (timestamp is not null) fields.Add($"\"timestamp\":{timestamp}");
        return "{" + string.Join(",", fields) + "}";
    }

    // ---- accepted and normalised -----------------------------------------------------------------------

    [Fact]
    public void A_valid_publish_is_accepted()
    {
        var parsed = Parse(Event());

        Assert.Null(parsed.EarlyOutcome);
        var e = Assert.IsType<CmsEvent>(parsed.Event);
        Assert.Equal(("a", CmsEventType.Publish, 1), (e.EntityId, e.Type, e.Version));
        Assert.Equal(7, parsed.Index);
        Assert.StartsWith("sha256:", parsed.PayloadChecksum);
    }

    // ---- checksums --------------------------------------------------------------------------------------

    [Fact]
    public void The_checksum_is_computed_over_the_canonical_payload_text_that_is_stored()
    {
        var e = Parse(Event(payload: "{ \"title\" : \"Hello\",\n \"n\" : 1 }")).Event!;

        var expected = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("""{"title":"Hello","n":1}""")));
        Assert.Equal("""{"title":"Hello","n":1}""", e.Payload);
        Assert.Equal(expected, e.PayloadChecksum);
    }

    /// <summary>
    /// The canonical form documented in docs/DESIGN.md, pinned: if this test changes, so must the document, and
    /// every client that reproduces checksums from it. ("@u" stands for a backslash-u escape, to keep the
    /// escapes readable and out of the way of tooling that rewrites them.)
    /// </summary>
    [Fact]
    public void The_canonical_form_is_exactly_what_the_design_document_says()
    {
        var input = """{"s":"+'&<>`/@u007f\"\\\n\t\r\b\f@u0001 é 😀 @u2028","n":[1.50,1E3,-0,0.1e-2],"b":true,"z":null,"o":{"b":1,"a":2}}"""
            .Replace("@u", "\\u");
        var expected = """{"s":"@u002B@u0027@u0026@u003C@u003E@u0060/@u007F@u0022\\\n\t\r\b\f@u0001 @u00E9 @uD83D@uDE00 @u2028","n":[1.50,1E3,-0,0.1e-2],"b":true,"z":null,"o":{"b":1,"a":2}}"""
            .Replace("@u", "\\u");

        var canonical = Parse(Event(payload: input)).Event!.Payload;

        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void The_same_data_formatted_differently_has_the_same_checksum_and_different_data_does_not()
    {
        var compact = Parse(Event(payload: """{"a":1,"b":[1,2]}""")).Event!.PayloadChecksum;
        var spaced = Parse(Event(payload: "{ \"a\" : 1 ,\n \"b\" : [ 1 , 2 ] }")).Event!.PayloadChecksum;
        var different = Parse(Event(payload: """{"a":1,"b":[1,3]}""")).Event!.PayloadChecksum;

        Assert.Equal(compact, spaced);
        Assert.NotEqual(compact, different);
    }

    [Fact]
    public void The_configured_algorithm_is_used_and_named_in_the_checksum()
    {
        var md5 = Parse(Event(), new IngestionOptions { ChecksumAlgorithm = "md5" }).Event!.PayloadChecksum!;

        Assert.StartsWith("md5:", md5);
        Assert.Equal(4 + 32, md5.Length);
    }

    [Fact]
    public void Deletes_and_rejected_events_have_no_checksum()
    {
        Assert.Null(Parse(Event(type: "\"delete\"")).PayloadChecksum);
        Assert.Null(Parse(Event(version: "0")).PayloadChecksum);
        Assert.Null(Parse(Event(payload: null)).PayloadChecksum);
    }

    [Theory]
    [InlineData("publish", CmsEventType.Publish)]
    [InlineData("PUBLISH", CmsEventType.Publish)]
    [InlineData("unPublish", CmsEventType.UnPublish)]
    [InlineData("unpublish", CmsEventType.UnPublish)]
    [InlineData("Delete", CmsEventType.Delete)]
    public void Event_types_are_matched_case_insensitively(string type, CmsEventType expected)
    {
        var parsed = Parse(Event(type: $"\"{type}\""));

        Assert.Equal(expected, parsed.Event!.Type);
    }

    [Fact]
    public void Ids_are_trimmed()
    {
        Assert.Equal("padded", Parse(Event(id: "\"  padded \"")).Event!.EntityId);
    }

    [Fact]
    public void Timestamps_with_an_offset_are_normalised_to_utc()
    {
        var e = Parse(Event(timestamp: "\"2024-01-01T02:00:00+02:00\"")).Event!;

        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), e.Timestamp);
        Assert.Equal(DateTimeKind.Utc, e.Timestamp.Kind);
    }

    [Fact]
    public void Payload_is_stored_as_compact_canonical_json()
    {
        var e = Parse(Event(payload: "{ \"b\" : [1, 2],\n  \"a\" : \"x\" }")).Event!;

        Assert.Equal("""{"b":[1,2],"a":"x"}""", e.Payload);
    }

    [Fact]
    public void Html_sensitive_characters_in_the_payload_are_emitted_as_unicode_escapes()
    {
        var e = Parse(Event(payload: """{"t":"<script>alert(1)</script>"}""")).Event!;

        Assert.DoesNotContain("<", e.Payload);
        Assert.Contains("\\u003Cscript\\u003E", e.Payload);
        // Same data once decoded: only the representation changed.
        using var roundTrip = JsonDocument.Parse(e.Payload!);
        Assert.Equal("<script>alert(1)</script>", roundTrip.RootElement.GetProperty("t").GetString());
    }

    [Fact]
    public void A_delete_ignores_any_version_and_payload_it_carries()
    {
        var parsed = Parse(Event(type: "\"delete\"", version: "\"nonsense\"", payload: "[1]"));

        var e = Assert.IsType<CmsEvent>(parsed.Event);
        Assert.Null(e.Version);
        Assert.Null(e.Payload);
    }

    [Fact]
    public void An_unknown_event_type_is_ignored_not_rejected()
    {
        var parsed = Parse(Event(type: "\"archive\"", version: null, payload: null));

        Assert.Null(parsed.Event);
        Assert.Equal(EventOutcome.IgnoredUnsupportedType, parsed.EarlyOutcome);
        Assert.Equal("a", parsed.EntityId);
    }

    [Fact]
    public void Control_characters_in_the_type_are_neutralised_for_the_ledger()
    {
        var parsed = Parse(Event(type: "\"bad\\u0000\\nthing\""));

        Assert.Equal("bad??thing", parsed.EventType);
    }

    // ---- rejected --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("42", ReasonCodes.EventNotObject)]
    [InlineData("\"text\"", ReasonCodes.EventNotObject)]
    [InlineData("null", ReasonCodes.EventNotObject)]
    [InlineData("[1]", ReasonCodes.EventNotObject)]
    public void Elements_that_are_not_objects_are_rejected(string json, string reason)
    {
        var parsed = Parse(json);

        Assert.Equal(EventOutcome.Rejected, parsed.EarlyOutcome);
        Assert.Equal(reason, parsed.Reason);
    }

    [Theory]
    // type
    [InlineData(null, "\"a\"", "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.MissingType)]
    [InlineData("\"\"", "\"a\"", "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.MissingType)]
    [InlineData("5", "\"a\"", "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.MissingType)]
    // id
    [InlineData("\"publish\"", null, "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.MissingId)]
    [InlineData("\"publish\"", "null", "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.MissingId)]
    [InlineData("\"publish\"", "\"   \"", "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.MissingId)]
    [InlineData("\"publish\"", "12", "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidId)]
    [InlineData("\"publish\"", "\"a\\u0007b\"", "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidId)]
    [InlineData("\"publish\"", "\"news/2024\"", "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidId)]
    [InlineData("\"publish\"", "\"news\\\\2024\"", "1", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidId)]
    // timestamp
    [InlineData("\"publish\"", "\"a\"", "1", "{}", null, ReasonCodes.MissingTimestamp)]
    [InlineData("\"publish\"", "\"a\"", "1", "{}", "\"2024-01-01T00:00:00\"", ReasonCodes.InvalidTimestamp)]
    [InlineData("\"publish\"", "\"a\"", "1", "{}", "\"yesterday\"", ReasonCodes.InvalidTimestamp)]
    [InlineData("\"publish\"", "\"a\"", "1", "{}", "\"2024-13-45T00:00:00Z\"", ReasonCodes.InvalidTimestamp)]
    [InlineData("\"publish\"", "\"a\"", "1", "{}", "1704067200", ReasonCodes.InvalidTimestamp)]
    [InlineData("\"publish\"", "\"a\"", "1", "{}", "\"2030-01-01T00:00:00Z\"", ReasonCodes.TimestampInFuture)]
    // version
    [InlineData("\"publish\"", "\"a\"", null, "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.MissingVersion)]
    [InlineData("\"unPublish\"", "\"a\"", "0", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidVersion)]
    [InlineData("\"publish\"", "\"a\"", "-3", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidVersion)]
    [InlineData("\"publish\"", "\"a\"", "1.5", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidVersion)]
    [InlineData("\"publish\"", "\"a\"", "\"1\"", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidVersion)]
    [InlineData("\"publish\"", "\"a\"", "9223372036854775808", "{}", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidVersion)]
    // payload
    [InlineData("\"publish\"", "\"a\"", "1", null, "\"2024-01-01T00:00:00Z\"", ReasonCodes.MissingPayload)]
    [InlineData("\"publish\"", "\"a\"", "1", "null", "\"2024-01-01T00:00:00Z\"", ReasonCodes.MissingPayload)]
    [InlineData("\"unPublish\"", "\"a\"", "1", "[1,2]", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidPayload)]
    [InlineData("\"publish\"", "\"a\"", "1", "\"text\"", "\"2024-01-01T00:00:00Z\"", ReasonCodes.InvalidPayload)]
    public void Invalid_events_are_rejected_with_a_specific_reason(
        string? type, string? id, string? version, string? payload, string? timestamp, string reason)
    {
        var parsed = Parse(Event(type, id, version, payload, timestamp));

        Assert.Null(parsed.Event);
        Assert.Equal(EventOutcome.Rejected, parsed.EarlyOutcome);
        Assert.Equal(reason, parsed.Reason);
    }

    [Fact]
    public void An_id_longer_than_the_limit_is_rejected()
    {
        var parsed = Parse(Event(id: $"\"{new string('x', EventParser.MaxIdLength + 1)}\""));

        Assert.Equal(ReasonCodes.InvalidId, parsed.Reason);
    }

    [Fact]
    public void An_id_at_the_limit_is_accepted()
    {
        var parsed = Parse(Event(id: $"\"{new string('x', EventParser.MaxIdLength)}\""));

        Assert.NotNull(parsed.Event);
    }

    [Fact]
    public void An_oversized_payload_is_rejected()
    {
        var options = new IngestionOptions { MaxPayloadBytes = 20 };

        var parsed = Parse(Event(payload: """{"title":"this is definitely more than twenty bytes"}"""), options);

        Assert.Equal(ReasonCodes.PayloadTooLarge, parsed.Reason);
    }

    [Fact]
    public void A_far_future_timestamp_is_rejected_but_a_small_clock_skew_is_tolerated()
    {
        var withinSkew = Now.AddMinutes(30).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var beyondSkew = Now.AddHours(3).ToString("yyyy-MM-ddTHH:mm:ssZ");

        Assert.NotNull(Parse(Event(timestamp: $"\"{withinSkew}\"")).Event);
        Assert.Equal(ReasonCodes.TimestampInFuture, Parse(Event(timestamp: $"\"{beyondSkew}\"")).Reason);
    }

    [Fact]
    public void A_rejected_event_keeps_what_could_be_read_for_the_ledger()
    {
        var parsed = Parse(Event(version: "0"));

        Assert.Equal("a", parsed.EntityId);
        Assert.Equal("publish", parsed.EventType);
        Assert.NotNull(parsed.Timestamp);
    }

    [Fact]
    public void Unpaired_surrogate_escapes_do_not_throw()
    {
        var parsed = Parse("""{"type":"publish","id":"a\ud800","version":1,"payload":{},"timestamp":"2024-01-01T00:00:00Z"}""");

        // Whichever way it is judged, the parser must come back with a verdict rather than blow up.
        Assert.True(parsed.Event is not null || parsed.EarlyOutcome == EventOutcome.Rejected);
    }
}
