using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lateral.Cms.Domain;
using Lateral.Cms.Integrity;

namespace Lateral.Cms.Ingestion;

/// <summary>
/// Validates and sanitises one element of an incoming batch.
/// </summary>
/// <remarks>
/// "Sanitising" here means normalising, not stripping markup: ids are trimmed and must be free of control
/// characters, timestamps are normalised to UTC, and payloads are re-serialised as compact canonical JSON
/// (which also emits HTML-sensitive and non-ASCII characters as \u escapes). Payload values are not
/// altered: they are data, and escaping belongs at the point where a consumer renders them.
/// </remarks>
public static partial class EventParser
{
    public const int MaxIdLength = 200;
    private const int MaxTypeLength = 64;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Iso8601WithOffset();

    public static ParsedEvent Parse(
        JsonElement element, int index, DateTime now, IngestionOptions options, IPayloadChecksum checksums)
    {
        try
        {
            return ParseCore(element, index, now, options, checksums);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            // E.g. a string holding an unpaired surrogate escape: valid JSON syntax, unusable text.
            return ParsedEvent.Rejected(index, null, null, ReasonCodes.MalformedEvent);
        }
    }

    private static ParsedEvent ParseCore(
        JsonElement element, int index, DateTime now, IngestionOptions options, IPayloadChecksum checksums)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return ParsedEvent.Rejected(index, null, null, ReasonCodes.EventNotObject);
        }

        if (!TryGetString(element, "type", out var typeText) || typeText.Trim().Length == 0)
        {
            return ParsedEvent.Rejected(index, null, null, ReasonCodes.MissingType);
        }

        var rawType = ForDisplay(typeText.Trim(), MaxTypeLength);
        var idValid = TryReadId(element, out var id, out var idReason);

        if (!TryMapType(typeText.Trim(), out var type))
        {
            // Event types "are not limited to" the three we know. Report it, don't fail the batch.
            return ParsedEvent.Unsupported(index, idValid ? id : null, rawType);
        }

        if (!idValid)
        {
            return ParsedEvent.Rejected(index, null, rawType, idReason!);
        }

        if (!TryReadTimestamp(element, now, options, out var timestamp, out var timestampReason))
        {
            return ParsedEvent.Rejected(index, id, rawType, timestampReason!);
        }

        if (type == CmsEventType.Delete)
        {
            // A delete carries no version or payload; anything sent is ignored.
            return ParsedEvent.Valid(index, rawType, new CmsEvent(id, type, null, timestamp, null));
        }

        if (!TryReadVersion(element, out var version, out var versionReason))
        {
            return ParsedEvent.Rejected(index, id, rawType, versionReason!, timestamp: timestamp);
        }

        if (!TryReadPayload(element, options, checksums, out var payload, out var checksum, out var payloadReason))
        {
            return ParsedEvent.Rejected(index, id, rawType, payloadReason!, version, timestamp);
        }

        return ParsedEvent.Valid(index, rawType, new CmsEvent(id, type, version, timestamp, payload, checksum));
    }

    private static bool TryMapType(string text, out CmsEventType type)
    {
        if (text.Equals("publish", StringComparison.OrdinalIgnoreCase))
        {
            type = CmsEventType.Publish;
        }
        else if (text.Equals("unPublish", StringComparison.OrdinalIgnoreCase))
        {
            type = CmsEventType.UnPublish;
        }
        else if (text.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            type = CmsEventType.Delete;
        }
        else
        {
            type = default;
            return false;
        }

        return true;
    }

    private static bool TryReadId(JsonElement obj, out string id, out string? reason)
    {
        id = "";
        reason = null;

        if (!obj.TryGetProperty("id", out var property) || property.ValueKind == JsonValueKind.Null)
        {
            reason = ReasonCodes.MissingId;
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            reason = ReasonCodes.InvalidId;
            return false;
        }

        var text = property.GetString()!.Trim();
        if (text.Length == 0)
        {
            reason = ReasonCodes.MissingId;
            return false;
        }

        // Ids appear in URL paths (/api/entities/{id}). A '/' or '\' cannot be carried in a single path segment,
        // so such an id could be stored but never fetched; refuse it loudly instead.
        if (text.Length > MaxIdLength || text.Any(c => char.IsControl(c) || c is '/' or '\\'))
        {
            reason = ReasonCodes.InvalidId;
            return false;
        }

        id = text;
        return true;
    }

    private static bool TryReadTimestamp(
        JsonElement obj, DateTime now, IngestionOptions options, out DateTime timestamp, out string? reason)
    {
        timestamp = default;
        reason = null;

        if (!obj.TryGetProperty("timestamp", out var property) || property.ValueKind == JsonValueKind.Null)
        {
            reason = ReasonCodes.MissingTimestamp;
            return false;
        }

        // An explicit offset is required: a bare local time is ambiguous.
        if (property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } text
            || !Iso8601WithOffset().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            reason = ReasonCodes.InvalidTimestamp;
            return false;
        }

        timestamp = parsed.UtcDateTime;
        if (timestamp > now + options.MaxFutureSkew)
        {
            reason = ReasonCodes.TimestampInFuture;
            return false;
        }

        return true;
    }

    private static bool TryReadVersion(JsonElement obj, out long version, out string? reason)
    {
        version = 0;
        reason = null;

        if (!obj.TryGetProperty("version", out var property) || property.ValueKind == JsonValueKind.Null)
        {
            reason = ReasonCodes.MissingVersion;
            return false;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out version) || version < 1)
        {
            reason = ReasonCodes.InvalidVersion;
            return false;
        }

        return true;
    }

    private static bool TryReadPayload(
        JsonElement obj, IngestionOptions options, IPayloadChecksum checksums,
        out string payload, out string? checksum, out string? reason)
    {
        payload = "";
        checksum = null;
        reason = null;

        if (!obj.TryGetProperty("payload", out var property) || property.ValueKind == JsonValueKind.Null)
        {
            reason = ReasonCodes.MissingPayload;
            return false;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            reason = ReasonCodes.InvalidPayload;
            return false;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            property.WriteTo(writer);
        }

        if (buffer.WrittenCount > options.MaxPayloadBytes)
        {
            reason = ReasonCodes.PayloadTooLarge;
            return false;
        }

        // The checksum covers exactly the bytes of the text we store, so it can be re-verified from the
        // stored value at any time.
        payload = Encoding.UTF8.GetString(buffer.WrittenSpan);
        checksum = checksums.Compute(buffer.WrittenSpan);
        return true;
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = "";
        return false;
    }

    /// <summary>Makes untrusted text safe to store in the ledger: no control characters, bounded length.</summary>
    private static string ForDisplay(string text, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in text)
        {
            if (builder.Length == maxLength)
            {
                break;
            }

            builder.Append(char.IsControl(c) ? '?' : c);
        }

        return builder.ToString();
    }
}
