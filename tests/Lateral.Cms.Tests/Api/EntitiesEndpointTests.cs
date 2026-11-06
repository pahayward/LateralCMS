using System.Net;
using System.Text.Json;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Api;

/// <summary>The consumer-facing API: who sees what, paging, and the admin override.</summary>
[Trait("req", "AP-01")]
[Trait("req", "AP-02")]
[Trait("req", "AP-03")]
[Trait("req", "AP-05")]
[Trait("req", "EV-15")]
[Trait("req", "SE-10")]
public class EntitiesEndpointTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private HttpClient Cms => factory.Cms();

    private HttpClient User => factory.User();

    private HttpClient Admin => factory.Admin();

    private static async Task<JsonDocument> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    // ---- visibility -------------------------------------------------------------------------------------

    [Fact]
    public async Task Users_see_only_published_and_not_disabled_entities_while_admins_see_everything()
    {
        var published = Evt.Id();
        var unpublished = Evt.Id();
        var disabled = Evt.Id();
        await factory.SendAsync(
            Evt.Publish(published, 1, 1),
            Evt.Publish(unpublished, 1, 1), Evt.UnPublish(unpublished, 1, 2),
            Evt.Publish(disabled, 1, 1));
        (await Admin.PostAsync($"/api/entities/{disabled}/disable", null)).EnsureSuccessStatusCode();

        var userIds = await User.ListIdsAsync();
        var adminIds = await Admin.ListIdsAsync();

        Assert.Contains(published, userIds);
        Assert.DoesNotContain(unpublished, userIds);
        Assert.DoesNotContain(disabled, userIds);
        Assert.Contains(published, adminIds);
        Assert.Contains(unpublished, adminIds);
        Assert.Contains(disabled, adminIds);
    }

    [Fact]
    public async Task Hidden_entities_look_exactly_like_missing_ones_to_users()
    {
        var unpublished = Evt.Id();
        await factory.SendAsync(Evt.Publish(unpublished, 1, 1), Evt.UnPublish(unpublished, 1, 2));

        var hidden = await User.GetEntityAsync(unpublished);
        var missing = await User.GetEntityAsync(Evt.Id());

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Apart from the per-request trace id, the two responses are the same.
        using var hiddenBody = await hidden.ReadJsonAsync();
        using var missingBody = await missing.ReadJsonAsync();
        foreach (var field in new[] { "status", "title", "detail", "type" })
        {
            Assert.Equal(
                missingBody.RootElement.GetProperty(field).ToString(),
                hiddenBody.RootElement.GetProperty(field).ToString());
        }
    }

    [Fact]
    public async Task Status_fields_are_only_shown_to_admins()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        using var asUser = await Read(await User.GetEntityAsync(id));
        using var asAdmin = await Read(await Admin.GetEntityAsync(id));

        Assert.False(asUser.RootElement.TryGetProperty("status", out _));
        var status = asAdmin.RootElement.GetProperty("status");
        Assert.True(status.GetProperty("isPublished").GetBoolean());
        Assert.False(status.GetProperty("isDisabledByAdmin").GetBoolean());
    }

    [Fact]
    public async Task A_query_string_cannot_widen_what_a_user_can_see()
    {
        var unpublished = Evt.Id();
        await factory.SendAsync(Evt.Publish(unpublished, 1, 1), Evt.UnPublish(unpublished, 1, 2));

        var ids = await User.ListIdsAsync("includeDisabled=true&isAdmin=true&status=all&showAll=1");

        Assert.DoesNotContain(unpublished, ids);
    }

    // ---- admin override --------------------------------------------------------------------------------

    [Fact]
    public async Task Disabling_hides_an_entity_from_users_and_enabling_brings_it_back()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        using var disabled = await Read(await Admin.PostAsync($"/api/entities/{id}/disable", null));
        Assert.True(disabled.RootElement.GetProperty("isDisabledByAdmin").GetBoolean());
        Assert.False(disabled.RootElement.GetProperty("visible").GetBoolean());
        Assert.Equal(CmsFactory.AdminName, disabled.RootElement.GetProperty("disabledBy").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await User.GetEntityAsync(id)).StatusCode);

        using var enabled = await Read(await Admin.PostAsync($"/api/entities/{id}/enable", null));
        Assert.True(enabled.RootElement.GetProperty("visible").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await User.GetEntityAsync(id)).StatusCode);
    }

    [Fact]
    public async Task Admin_actions_are_logged_with_who_and_what()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        await Admin.PostAsync($"/api/entities/{id}/disable", null);
        await Admin.PostAsync($"/api/entities/{id}/enable", null);

        var audit = factory.Logs.Entries.Where(e => e.Message.Contains(id) && e.Message.StartsWith("Admin ")).Select(e => e.Message).ToList();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, line => Assert.Contains(CmsFactory.AdminName, line));
        Assert.Contains(audit, line => line.Contains("disabled=True"));
        Assert.Contains(audit, line => line.Contains("disabled=False"));
    }

    [Fact]
    public async Task Disabling_twice_is_idempotent_and_keeps_the_original_audit_fields()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        using var first = await Read(await Admin.PostAsync($"/api/entities/{id}/disable", null));
        using var second = await Read(await Admin.PostAsync($"/api/entities/{id}/disable", null));

        Assert.Equal(
            first.RootElement.GetProperty("disabledAt").GetString(),
            second.RootElement.GetProperty("disabledAt").GetString());
    }

    [Fact]
    public async Task Disabling_leaves_the_cms_data_untouched()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 3, 1, new { title = "as published" }));

        await Admin.PostAsync($"/api/entities/{id}/disable", null);

        using var entity = await Read(await Admin.GetEntityAsync(id));
        Assert.Equal(3, entity.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("as published", entity.RootElement.GetProperty("payload").GetProperty("title").GetString());
        Assert.True(entity.RootElement.GetProperty("status").GetProperty("isPublished").GetBoolean());
    }

    [Fact]
    public async Task Disabling_an_unknown_entity_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PostAsync($"/api/entities/{Evt.Id()}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PostAsync($"/api/entities/{Evt.Id()}/enable", null)).StatusCode);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Entities_cannot_be_changed_or_deleted_through_the_api_by_anyone(string method)
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        foreach (var client in new[] { User, Admin, Cms })
        {
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/api/entities/{id}")
            {
                Content = Evt.Json(new { payload = new { title = "tampered" } }),
            });

            Assert.True(
                response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                $"{method} returned {(int)response.StatusCode}");
        }

        using var entity = await Read(await Admin.GetEntityAsync(id));
        Assert.Equal($"{id} v1", entity.RootElement.GetProperty("payload").GetProperty("title").GetString());
    }

    // ---- paging ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_cursors_get_400()
    {
        var response = await User.GetAsync("/api/entities?cursor=%25%25%25not-a-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await response.ReadJsonAsync();
        Assert.Equal("invalid_cursor", problem.RootElement.GetProperty("code").GetString());
    }
}

/// <summary>Paging needs a database of known size, so it gets its own, with a small page limit.</summary>
public class EntityPagingTests : IClassFixture<EntityPagingTests.SmallPagesFactory>
{
    public sealed class SmallPagesFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
        [
            new("Api:DefaultPageSize", "3"),
            new("Api:MaxPageSize", "5"),
        ];
    }

    private readonly SmallPagesFactory _factory;

    public EntityPagingTests(SmallPagesFactory factory) => _factory = factory;

    // Always the same eight entities (re-seeding is a harmless replay), so tests can rely on the total.
    private async Task<string[]> Seed()
    {
        var ids = Enumerable.Range(0, 8).Select(i => $"item-{i:D3}").ToArray();
        await _factory.SendAsync([.. ids.Select(id => Evt.Publish(id, 1, 1))]);
        return ids;
    }

    [Fact]
    public async Task Pages_follow_the_cursor_in_stable_id_order_without_gaps_or_repeats()
    {
        var seeded = await Seed();

        var all = await _factory.User().ListIdsAsync("limit=3");

        Assert.Equal(seeded, all);
    }

    [Fact]
    public async Task The_default_page_size_applies_and_oversized_or_tiny_limits_are_clamped()
    {
        await Seed();
        var user = _factory.User();

        using var first = await Read(await user.GetAsync("/api/entities"));
        Assert.Equal(3, first.RootElement.GetProperty("items").GetArrayLength());
        Assert.NotNull(first.RootElement.GetProperty("nextCursor").GetString());

        using var big = await Read(await user.GetAsync("/api/entities?limit=1000"));
        Assert.Equal(5, big.RootElement.GetProperty("items").GetArrayLength());

        using var small = await Read(await user.GetAsync("/api/entities?limit=0"));
        Assert.Equal(1, small.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_final_page_has_no_next_cursor()
    {
        await Seed();
        var user = _factory.User();

        using var first = await Read(await user.GetAsync("/api/entities?limit=5"));
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();
        using var last = await Read(await user.GetAsync($"/api/entities?limit=5&cursor={Uri.EscapeDataString(cursor!)}"));

        Assert.Equal(3, last.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, last.RootElement.GetProperty("nextCursor").ValueKind);
    }

    private static async Task<JsonDocument> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }
}
