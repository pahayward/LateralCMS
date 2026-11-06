using System.Net;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Auth;

/// <summary>Every kind of caller against every endpoint, so no pairing is left to inference.</summary>
[Trait("req", "SE-01")]
[Trait("req", "SE-02")]
[Trait("req", "IN-01")]
public class RoleMatrixTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private const string GetTarget = "matrix-get";
    private const string ToggleTarget = "matrix-toggle";

    // endpoint, caller, expected status
    public static TheoryData<string, string, HttpStatusCode> Matrix()
    {
        var data = new TheoryData<string, string, HttpStatusCode>();
        void Row(string endpoint, HttpStatusCode anonymous, HttpStatusCode cms, HttpStatusCode user, HttpStatusCode admin)
        {
            data.Add(endpoint, "anonymous", anonymous);
            data.Add(endpoint, "cms", cms);
            data.Add(endpoint, "user", user);
            data.Add(endpoint, "admin", admin);
        }

        const HttpStatusCode ok = HttpStatusCode.OK;
        const HttpStatusCode unauthorized = HttpStatusCode.Unauthorized;
        const HttpStatusCode forbidden = HttpStatusCode.Forbidden;
        const HttpStatusCode notFound = HttpStatusCode.NotFound;

        Row("POST /cms/events",                unauthorized, ok,        forbidden, forbidden);
        Row("GET /api/entities",               unauthorized, forbidden, ok,        ok);
        Row("GET /api/entities/{id}",          unauthorized, forbidden, ok,        ok);
        Row("POST /api/entities/{id}/disable", unauthorized, forbidden, forbidden, ok);
        Row("POST /api/entities/{id}/enable",  unauthorized, forbidden, forbidden, ok);
        Row("GET /health",                     ok,           ok,        ok,        ok);

        // The CMS follows its own batches and an admin investigates them; users have no business with either.
        Row("GET /cms/batches/{batchId:guid}", unauthorized, ok,        forbidden, ok);
        Row("GET /api/admin/ingestion",        unauthorized, forbidden, forbidden, ok);

        // Allowed for an admin, but the batch used here does not exist, so "allowed" shows as 404 rather than 200.
        Row("POST /api/admin/ingestion/{batchId:guid}/requeue", unauthorized, forbidden, forbidden, notFound);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Each_caller_gets_exactly_the_access_its_role_allows(string endpoint, string caller, HttpStatusCode expected)
    {
        // Idempotent set-up: both entities exist and are published.
        using var setup = await factory.SendAsync(Evt.Publish(GetTarget, 1, 1), Evt.Publish(ToggleTarget, 1, 1));
        var batchId = setup.RootElement.GetProperty("batchId").GetString();

        HttpClient client = caller switch
        {
            "anonymous" => factory.CreateClient(),
            "cms" => factory.Cms(),
            "user" => factory.User(),
            "admin" => factory.Admin(),
            _ => throw new ArgumentOutOfRangeException(nameof(caller)),
        };

        var request = endpoint switch
        {
            "POST /cms/events" => new HttpRequestMessage(HttpMethod.Post, "/cms/events") { Content = Evt.Raw("[]") },
            "GET /api/entities" => new HttpRequestMessage(HttpMethod.Get, "/api/entities"),
            "GET /api/entities/{id}" => new HttpRequestMessage(HttpMethod.Get, $"/api/entities/{GetTarget}"),
            "POST /api/entities/{id}/disable" => new HttpRequestMessage(HttpMethod.Post, $"/api/entities/{ToggleTarget}/disable"),
            "POST /api/entities/{id}/enable" => new HttpRequestMessage(HttpMethod.Post, $"/api/entities/{ToggleTarget}/enable"),
            "GET /health" => new HttpRequestMessage(HttpMethod.Get, "/health"),
            "GET /cms/batches/{batchId:guid}" => new HttpRequestMessage(HttpMethod.Get, $"/cms/batches/{batchId}"),
            "GET /api/admin/ingestion" => new HttpRequestMessage(HttpMethod.Get, "/api/admin/ingestion"),
            "POST /api/admin/ingestion/{batchId:guid}/requeue" =>
                new HttpRequestMessage(HttpMethod.Post, $"/api/admin/ingestion/{Guid.NewGuid()}/requeue"),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public void The_matrix_covers_every_mapped_endpoint()
    {
        var mapped = factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Single()} /{e.RoutePattern.RawText!.Trim('/')}")
            .ToHashSet();

        var covered = Matrix().Select(row => (string)row[0]).ToHashSet();

        // An endpoint added later without a row here fails this test, so the matrix cannot silently go stale.
        Assert.Empty(mapped.Except(covered));
    }
}
