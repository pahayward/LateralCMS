using System.Net;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Api;

/// <summary>What the service exposes at all (no route changes an entity's content) and how it behaves in Production.</summary>
public class RouteAndTransportTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    [Fact]
    [Trait("req", "AP-04")]
    public void No_route_updates_replaces_or_deletes_an_entity_the_only_writes_are_ingestion_the_admin_override_and_the_queue()
    {
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} /{e.RoutePattern.RawText!.Trim('/')}"))
            .ToHashSet();

        var writes = routes.Where(r => !r.StartsWith("GET ", StringComparison.Ordinal) && !r.StartsWith("HEAD ", StringComparison.Ordinal)).Order().ToList();

        // Anything added later that can write must be named here, on purpose, and justified in the design.
        Assert.Equal(
            [
                "DELETE /api/admin/users/{username}",
                "DELETE /api/assets/{id:guid}",
                "DELETE /cms/assets/uploads/{uploadId:guid}",
                "PATCH /api/admin/users/{username}",
                "POST /api/admin/assets/rescan-outdated",
                "POST /api/admin/assets/{id:guid}/requeue",
                "POST /api/admin/assets/{id:guid}/rescan",
                "POST /api/admin/ingestion/{batchId:guid}/requeue",
                "POST /api/admin/users",
                "POST /api/admin/users/{username}/password",
                "POST /api/admin/users/{username}/revoke-sessions",
                "POST /api/auth/login",
                "POST /api/auth/logout",
                "POST /api/auth/password",
                "POST /api/entities/{id}/disable",
                "POST /api/entities/{id}/enable",
                "POST /cms/assets/uploads",
                "POST /cms/assets/uploads/{uploadId:guid}/complete",
                "POST /cms/events",
                "PUT /cms/assets/uploads/{uploadId:guid}/parts/{partNumber:int}",
                "PUT /cms/assets/{key}",
            ],
            writes);

        // None of the reads or writes on entities takes a body that could carry new content.
        // Nothing can change an entity: the only PUT and DELETE routes are for assets.
        Assert.DoesNotContain(routes, r => r.Contains("/entities", StringComparison.Ordinal)
                                           && (r.StartsWith("PUT ", StringComparison.Ordinal) || r.StartsWith("PATCH ", StringComparison.Ordinal)
                                               || r.StartsWith("DELETE ", StringComparison.Ordinal)));
    }

    [Theory]
    [Trait("req", "AP-04")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task An_attempt_to_change_or_remove_an_entity_through_the_api_is_refused_even_for_an_admin(string method)
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        var response = await factory.Admin().SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/api/entities/{id}")
        {
            Content = Evt.Raw("""{"title":"changed"}"""),
        });

        Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, $"{method} gave {(int)response.StatusCode}");
        using var after = await (await factory.Admin().GetEntityAsync(id)).ReadJsonAsync();
        Assert.Equal(1, after.RootElement.GetProperty("version").GetInt32());
    }

    public sealed class ProductionFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("HTTPS_PORT", "443")];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
        }
    }

    [Fact]
    [Trait("req", "SE-08")]
    public async Task In_production_plain_http_is_redirected_to_https_and_https_responses_carry_hsts()
    {
        using var production = new ProductionFactory();
        var plain = production.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var redirect = await plain.GetAsync("/health");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, redirect.StatusCode);
        Assert.StartsWith("https://", redirect.Headers.Location!.ToString());

        var secure = production.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://service.example.test"),
            AllowAutoRedirect = false,
        });
        var response = await secure.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    [Trait("req", "SE-08")]
    public async Task Outside_production_plain_http_is_served_so_local_runs_need_no_certificate()
    {
        var response = await factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }
}
