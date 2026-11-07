using System.Net;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Auth;

/// <summary>Every kind of caller against every endpoint, so no pairing is left to inference.</summary>
[Trait("req", "SE-01")]
[Trait("req", "SE-02")]
[Trait("req", "IN-01")]
[Trait("req", "BL-02")]
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

        // Assets. The CMS uploads; the CMS and admins follow an asset; users and admins download; admins administer. The ids
        // used here do not exist, so "allowed" shows as 404 (or 409 where the route checks state first).
        const HttpStatusCode accepted = HttpStatusCode.Accepted;
        const HttpStatusCode created = HttpStatusCode.Created;
        const HttpStatusCode conflict = HttpStatusCode.Conflict;
        Row("PUT /cms/assets/{key}",                                          unauthorized, accepted,  forbidden, forbidden);
        Row("POST /cms/assets/uploads",                                       unauthorized, created,   forbidden, forbidden);
        Row("PUT /cms/assets/uploads/{uploadId:guid}/parts/{partNumber:int}", unauthorized, notFound,  forbidden, forbidden);
        Row("POST /cms/assets/uploads/{uploadId:guid}/complete",              unauthorized, notFound,  forbidden, forbidden);
        Row("DELETE /cms/assets/uploads/{uploadId:guid}",                     unauthorized, notFound,  forbidden, forbidden);
        Row("GET /cms/assets/{id:guid}",                                      unauthorized, notFound,  forbidden, notFound);
        Row("GET /api/assets/{id:guid}",                                      unauthorized, forbidden, notFound,  notFound);
        Row("HEAD /api/assets/{id:guid}",                                     unauthorized, forbidden, notFound,  notFound);
        Row("DELETE /api/assets/{id:guid}",                                   unauthorized, forbidden, forbidden, notFound);
        Row("GET /api/admin/assets",                                          unauthorized, forbidden, forbidden, ok);
        Row("POST /api/admin/assets/{id:guid}/rescan",                        unauthorized, forbidden, forbidden, conflict);
        Row("POST /api/admin/assets/{id:guid}/requeue",                       unauthorized, forbidden, forbidden, conflict);
        Row("POST /api/admin/assets/rescan-outdated",                         unauthorized, forbidden, forbidden, ok);

        // Signing in, who am I, and your own password. Anyone may try to sign in (an empty body is a 400 for all); the rest need a
        // sign-in. The accounts here are defined in configuration, so changing their password is a 409: it is done there.
        const HttpStatusCode badRequest = HttpStatusCode.BadRequest;
        const HttpStatusCode noContent = HttpStatusCode.NoContent;
        Row("POST /api/auth/login",                                           badRequest,   badRequest, badRequest, badRequest);
        Row("POST /api/auth/logout",                                          unauthorized, noContent, noContent, noContent);
        Row("GET /api/auth/me",                                               unauthorized, ok,        ok,        ok);
        Row("POST /api/auth/password",                                        unauthorized, conflict,  conflict,  conflict);

        // People, settings and storage are for administrators. The bodies here are empty or name nobody, so "allowed" shows as 400 or 404.
        Row("GET /api/admin/users",                                           unauthorized, forbidden, forbidden, ok);
        Row("POST /api/admin/users",                                          unauthorized, forbidden, forbidden, badRequest);
        Row("PATCH /api/admin/users/{username}",                              unauthorized, forbidden, forbidden, badRequest);
        Row("POST /api/admin/users/{username}/password",                      unauthorized, forbidden, forbidden, notFound);
        Row("POST /api/admin/users/{username}/revoke-sessions",               unauthorized, forbidden, forbidden, notFound);
        Row("DELETE /api/admin/users/{username}",                             unauthorized, forbidden, forbidden, notFound);
        Row("GET /api/admin/settings",                                        unauthorized, forbidden, forbidden, ok);
        Row("GET /api/admin/storage",                                         unauthorized, forbidden, forbidden, ok);
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
            "PUT /cms/assets/{key}" => new HttpRequestMessage(HttpMethod.Put, $"/cms/assets/matrix-{Guid.NewGuid():N}") { Content = new StringContent("matrix content") },
            "POST /cms/assets/uploads" => new HttpRequestMessage(HttpMethod.Post, "/cms/assets/uploads") { Content = Evt.Raw("""{"key":"matrix","size":10}""") },
            "PUT /cms/assets/uploads/{uploadId:guid}/parts/{partNumber:int}" => new HttpRequestMessage(HttpMethod.Put, $"/cms/assets/uploads/{Guid.NewGuid()}/parts/1") { Content = new StringContent("x") },
            "POST /cms/assets/uploads/{uploadId:guid}/complete" => new HttpRequestMessage(HttpMethod.Post, $"/cms/assets/uploads/{Guid.NewGuid()}/complete") { Content = Evt.Raw("""{"parts":[{"partNumber":1}]}""") },
            "DELETE /cms/assets/uploads/{uploadId:guid}" => new HttpRequestMessage(HttpMethod.Delete, $"/cms/assets/uploads/{Guid.NewGuid()}"),
            "GET /cms/assets/{id:guid}" => new HttpRequestMessage(HttpMethod.Get, $"/cms/assets/{Guid.NewGuid()}"),
            "GET /api/assets/{id:guid}" => new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{Guid.NewGuid()}"),
            "HEAD /api/assets/{id:guid}" => new HttpRequestMessage(HttpMethod.Head, $"/api/assets/{Guid.NewGuid()}"),
            "DELETE /api/assets/{id:guid}" => new HttpRequestMessage(HttpMethod.Delete, $"/api/assets/{Guid.NewGuid()}"),
            "GET /api/admin/assets" => new HttpRequestMessage(HttpMethod.Get, "/api/admin/assets"),
            "POST /api/admin/assets/{id:guid}/rescan" => new HttpRequestMessage(HttpMethod.Post, $"/api/admin/assets/{Guid.NewGuid()}/rescan"),
            "POST /api/admin/assets/{id:guid}/requeue" => new HttpRequestMessage(HttpMethod.Post, $"/api/admin/assets/{Guid.NewGuid()}/requeue"),
            "POST /api/admin/assets/rescan-outdated" => new HttpRequestMessage(HttpMethod.Post, "/api/admin/assets/rescan-outdated"),
            "POST /api/auth/login" => new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = Evt.Raw("{}") },
            "POST /api/auth/logout" => new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout"),
            "GET /api/auth/me" => new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"),
            "POST /api/auth/password" => new HttpRequestMessage(HttpMethod.Post, "/api/auth/password") { Content = Evt.Raw("{}") },
            "GET /api/admin/users" => new HttpRequestMessage(HttpMethod.Get, "/api/admin/users"),
            "POST /api/admin/users" => new HttpRequestMessage(HttpMethod.Post, "/api/admin/users") { Content = Evt.Raw("{}") },
            "PATCH /api/admin/users/{username}" => new HttpRequestMessage(HttpMethod.Patch, "/api/admin/users/nobody") { Content = Evt.Raw("{}") },
            "POST /api/admin/users/{username}/password" => new HttpRequestMessage(HttpMethod.Post, "/api/admin/users/nobody/password") { Content = Evt.Raw("{}") },
            "POST /api/admin/users/{username}/revoke-sessions" => new HttpRequestMessage(HttpMethod.Post, "/api/admin/users/nobody/revoke-sessions"),
            "DELETE /api/admin/users/{username}" => new HttpRequestMessage(HttpMethod.Delete, "/api/admin/users/nobody"),
            "GET /api/admin/settings" => new HttpRequestMessage(HttpMethod.Get, "/api/admin/settings"),
            "GET /api/admin/storage" => new HttpRequestMessage(HttpMethod.Get, "/api/admin/storage"),
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
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} /{e.RoutePattern.RawText!.Trim('/')}"))
            .ToHashSet();

        var covered = Matrix().Select(row => (string)row[0]).ToHashSet();

        // An endpoint added later without a row here fails this test, so the matrix cannot silently go stale.
        Assert.Empty(mapped.Except(covered));
    }
}
