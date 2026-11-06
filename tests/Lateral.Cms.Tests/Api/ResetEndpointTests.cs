using System.Net;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Api;

/// <summary>
/// The test-only reset (POST /api/admin/reset): an admin can erase everything and start again, nobody else can,
/// and it does not exist unless it is switched on.
/// </summary>
[Trait("req", "DV-02")]
public class ResetEndpointTests : IClassFixture<ResetEndpointTests.ResetEnabledFactory>
{
    public sealed class ResetEnabledFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Admin:AllowReset", "true")];
    }

    private const string Url = "/api/admin/reset";
    private const string Confirmation = "{\"confirm\":\"reset-all-data\"}";

    private readonly ResetEnabledFactory _factory;

    public ResetEndpointTests(ResetEnabledFactory factory) => _factory = factory;

    private Task<HttpResponseMessage> ResetAsync() => _factory.Admin().PostAsync(Url, Evt.Raw(Confirmation));

    private long Count(string table) => (long)_factory.Sql($"SELECT COUNT(*) FROM {table}")[0][0]!;

    [Fact]
    public async Task An_admin_with_the_confirmation_erases_entities_tombstones_and_the_ledger()
    {
        await ResetAsync();
        var (kept1, kept2, deleted) = (Evt.Id(), Evt.Id(), Evt.Id());
        await _factory.SendAsync(
            Evt.Publish(kept1, 1, 1), Evt.Publish(kept2, 1, 1), Evt.Publish(deleted, 1, 1), Evt.Delete(deleted, 2));

        var response = await ResetAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = await response.ReadJsonAsync();
        Assert.Equal(2, result.RootElement.GetProperty("entities").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("tombstones").GetInt32());
        Assert.Equal(4, result.RootElement.GetProperty("ledgerRows").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("batches").GetInt32());

        Assert.Equal(0, Count("Entities"));
        Assert.Equal(0, Count("Tombstones"));
        Assert.Equal(0, Count("Events"));
        Assert.Empty(await _factory.Admin().ListIdsAsync());
    }

    [Fact]
    public async Task After_a_reset_events_that_a_tombstone_had_blocked_are_accepted_again()
    {
        await ResetAsync();
        var id = Evt.Id();
        await _factory.SendAsync(Evt.Publish(id, 1, 10), Evt.Delete(id, 20));

        // Before the reset the delete wins over an older publish.
        using (var blocked = await _factory.SendAsync(Evt.Publish(id, 1, 10)))
        {
            Assert.Equal(["ignored_stale"], blocked.Outcomes());
            Assert.Equal(["superseded_by_delete"], blocked.Reasons());
        }

        await ResetAsync();

        // The slate is clean, so the same event is simply new.
        using var accepted = await _factory.SendAsync(Evt.Publish(id, 1, 10));
        Assert.Equal(["applied"], accepted.Outcomes());
        Assert.Equal(HttpStatusCode.OK, (await _factory.Admin().GetEntityAsync(id)).StatusCode);
    }

    [Fact]
    public async Task Resetting_an_empty_service_succeeds_with_zero_counts()
    {
        await ResetAsync();

        var response = await ResetAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = await response.ReadJsonAsync();
        Assert.Equal(0, result.RootElement.GetProperty("entities").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("ledgerRows").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"confirm\":null}")]
    [InlineData("{\"confirm\":\"yes\"}")]
    [InlineData("{\"confirm\":\"RESET-ALL-DATA\"}")]
    public async Task Without_the_exact_confirmation_nothing_is_erased(string? body)
    {
        var id = Evt.Id();
        await _factory.SendAsync(Evt.Publish(id, 1, 1));

        var response = await _factory.Admin().PostAsync(Url, body is null ? null : Evt.Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await response.ReadJsonAsync();
        Assert.Equal("confirmation_required", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _factory.Admin().GetEntityAsync(id)).StatusCode);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-password", HttpStatusCode.Unauthorized)]
    [InlineData("cms", HttpStatusCode.Forbidden)]
    [InlineData("user", HttpStatusCode.Forbidden)]
    public async Task Only_an_admin_may_reset_and_the_data_survives_anyone_else(string caller, HttpStatusCode expected)
    {
        var id = Evt.Id();
        await _factory.SendAsync(Evt.Publish(id, 1, 1));

        HttpClient client = caller switch
        {
            "anonymous" => _factory.CreateClient(),
            "wrong-password" => _factory.ClientFor(CmsFactory.AdminName, "not-the-password"),
            "cms" => _factory.Cms(),
            "user" => _factory.User(),
            _ => throw new ArgumentOutOfRangeException(nameof(caller)),
        };

        var response = await client.PostAsync(Url, Evt.Raw(Confirmation));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.Admin().GetEntityAsync(id)).StatusCode);
    }

    [Fact]
    public async Task The_reset_is_logged_with_who_and_how_much_but_never_the_content()
    {
        const string secret = "SECRET-PAYLOAD-TEXT-9f3a";
        await _factory.SendAsync(Evt.Publish(Evt.Id(), 1, 1, new { title = secret }));
        static bool IsResetLine(CapturingLoggerProvider.Entry entry) =>
            entry.Message.StartsWith("Admin ", StringComparison.Ordinal) && entry.Message.Contains("RESET");
        var before = _factory.Logs.Entries.Count(IsResetLine);

        await ResetAsync();

        // The factory is shared with the other tests in this class, so look at what this reset added.
        var lines = _factory.Logs.Entries.Where(IsResetLine).ToList();
        Assert.Equal(before + 1, lines.Count);
        var line = lines[^1];
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, line.Level);
        Assert.Contains(CmsFactory.AdminName, line.Message);
        Assert.Contains("entities", line.Message);
        Assert.DoesNotContain(secret, line.Message);
    }

    [Fact]
    public void The_route_is_mapped_only_because_it_was_switched_on()
    {
        Assert.Contains(RoutePatterns(_factory), pattern => pattern == "api/admin/reset");
    }

    internal static IEnumerable<string> RoutePatterns(CmsFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!.Trim('/'));
}

/// <summary>With the shipped defaults the reset does not exist: it answers 404 to everyone.</summary>
public class ResetDisabledByDefaultTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    [Fact]
    public async Task An_admin_gets_404_because_the_route_is_not_mapped()
    {
        var response = await factory.Admin().PostAsync("/api/admin/reset", Evt.Raw("{\"confirm\":\"reset-all-data\"}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void The_setting_itself_defaults_to_off_even_with_no_configuration_at_all()
    {
        Assert.False(new Lateral.Cms.Admin.AdminOptions().AllowReset);
    }

    [Fact]
    public void The_route_is_absent_from_the_endpoint_table()
    {
        Assert.DoesNotContain(ResetEndpointTests.RoutePatterns(factory), pattern => pattern == "api/admin/reset");
    }

    [Fact]
    public async Task Data_is_untouched_by_the_attempt()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        await factory.Admin().PostAsync("/api/admin/reset", Evt.Raw("{\"confirm\":\"reset-all-data\"}"));

        Assert.Equal(HttpStatusCode.OK, (await factory.Admin().GetEntityAsync(id)).StatusCode);
    }
}
