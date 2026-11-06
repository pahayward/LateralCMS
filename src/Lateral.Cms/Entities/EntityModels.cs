using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lateral.Cms.Entities;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    [Range(1, 1000)]
    public int DefaultPageSize { get; set; } = 50;

    [Range(1, 1000)]
    public int MaxPageSize { get; set; } = 100;
}

/// <summary>
/// Already-valid JSON text written straight to the response, so the stored payload is neither parsed nor
/// re-serialised on the read path.
/// </summary>
[JsonConverter(typeof(RawJsonConverter))]
public readonly record struct RawJson(string Json);

public sealed class RawJsonConverter : JsonConverter<RawJson>
{
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("RawJson is write-only.");

    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.Json, skipInputValidation: true);
}

/// <summary>
/// An entity as served to consumers. <see cref="Checksum"/> is the checksum stored with the payload, the same
/// value the CMS was given when it published it. <see cref="Status"/> is only present for admins.
/// </summary>
public sealed record EntityDto(
    string Id,
    long Version,
    string Checksum,
    DateTime UpdatedAt,
    RawJson Payload,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EntityStatusDto? Status);

public sealed record EntityStatusDto(bool IsPublished, bool IsDisabledByAdmin, string? DisabledBy, DateTime? DisabledAt);

public sealed record EntityPage(IReadOnlyList<EntityDto> Items, string? NextCursor);

/// <summary>Result of an admin enable/disable: the state that decides whether consumers can see the entity.</summary>
public sealed record AdminEntityState(
    string Id,
    bool IsPublished,
    bool IsDisabledByAdmin,
    bool Visible,
    string? DisabledBy,
    DateTime? DisabledAt);
