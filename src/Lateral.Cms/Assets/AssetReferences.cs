using System.Text.Json;

namespace Lateral.Cms.Assets;

/// <summary>
/// Which files a record names. The payload's shape belongs to the CMS, so the rule is the plainest one that cannot miss: every JSON
/// string value that is a GUID (in its usual 36-character form) is taken as a file id. Ids that match no file are harmless.
/// </summary>
public static class AssetReferences
{
    public static HashSet<Guid> In(string? payload)
    {
        var found = new HashSet<Guid>();
        if (string.IsNullOrEmpty(payload))
        {
            return found;
        }

        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(payload));
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.String
                    && reader.GetString() is { Length: 36 } text
                    && Guid.TryParseExact(text, "D", out var id))
                {
                    found.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // Stored payloads are canonical JSON we wrote ourselves; if one ever is not, it names nothing.
        }

        return found;
    }
}
