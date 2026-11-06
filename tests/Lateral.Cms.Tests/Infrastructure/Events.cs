using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Lateral.Cms.Tests.Infrastructure;

/// <summary>Builders for wire-format events, and small helpers for the test HTTP calls.</summary>
public static class Evt
{
    public static string Id() => $"e-{Guid.NewGuid():N}";

    /// <summary>Minutes after 2024-01-01T00:00:00Z, as an ISO-8601 string.</summary>
    public static string T(int minute) =>
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute).ToString("yyyy-MM-ddTHH:mm:ssZ");

    public static object Publish(string id, long version, int minute, object? payload = null) =>
        new { type = "publish", id, version, payload = payload ?? new { title = $"{id} v{version}" }, timestamp = T(minute) };

    public static object UnPublish(string id, long version, int minute, object? payload = null) =>
        new { type = "unPublish", id, version, payload = payload ?? new { title = $"{id} v{version}" }, timestamp = T(minute) };

    public static object Delete(string id, int minute) => new { type = "delete", id, timestamp = T(minute) };

    public static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");

    public static async Task<HttpResponseMessage> PostAsync(this HttpClient client, params object[] events) =>
        await client.PostAsync("/cms/events", Json(events));

    /// <summary>Posts a batch that must be accepted (200) and returns the parsed response.</summary>
    public static async Task<JsonDocument> PostOkAsync(this HttpClient client, params object[] events)
    {
        var response = await client.PostAsync(events);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected 200, got {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    public static string[] Outcomes(this JsonDocument result) =>
        [.. result.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("outcome").GetString()!)];

    public static string[] Reasons(this JsonDocument result) =>
        [.. result.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.TryGetProperty("reason", out var reason) ? reason.GetString()! : "")];

    public static async Task<JsonDocument> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    public static Task<HttpResponseMessage> GetEntityAsync(this HttpClient client, string id) =>
        client.GetAsync($"/api/entities/{Uri.EscapeDataString(id)}");

    /// <summary>Every entity id visible to this caller, following all pages.</summary>
    public static async Task<List<string>> ListIdsAsync(this HttpClient client, string query = "")
    {
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var url = "/api/entities?" + query + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var response = await client.GetAsync(url);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            using var page = await response.ReadJsonAsync();
            ids.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
            cursor = page.RootElement.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return ids;
    }
}
