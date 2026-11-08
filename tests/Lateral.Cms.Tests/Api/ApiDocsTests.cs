using System.Net;

using System.Text;
using System.Text.Json;
using Lateral.Cms.ApiDocs;

using Lateral.Cms.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Api;

/// <summary>
/// Swagger: the OpenAPI document and the page that shows it. They must be behind sign-in, describe exactly the routes the service
/// maps and the callers the role matrix proves, serve the page under a policy that still allows no inline or generated script, and
/// switch off.
/// </summary>
[Trait("req", "DX-03")]
public class ApiDocsTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private const string Document = "/swagger/v1/swagger.json";

    private static string Plain(string route) => System.Text.RegularExpressions.Regex.Replace(route, @"\{(\w+):\w+\}", "{$1}");

    private async Task<JsonDocument> ReadDocumentAsync()
    {
        var response = await factory.Admin().GetAsync(Document);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static List<(string Method, string Path, JsonElement Operation)> Operations(JsonDocument document) =>
        document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(op => (op.Name.ToUpperInvariant(), path.Name, op.Value)))
            .ToList();

    [Theory]
    [InlineData(Document)]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/swagger-ui-bundle.js")]
    public async Task Nothing_of_it_is_served_without_signing_in_and_a_browser_is_asked_for_a_password(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Basic");
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData("cms")]
    [InlineData("user")]
    [InlineData("admin")]
    public async Task Any_signed_in_caller_gets_the_document_and_the_page(string caller)
    {
        using var client = caller switch { "cms" => factory.Cms(), "user" => factory.User(), _ => factory.Admin() };

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Document)).StatusCode);

        var page = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("swagger-ui", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_page_has_its_own_policy_which_allows_only_its_own_scripts_and_no_generated_code()
    {
        var page = await factory.Admin().GetAsync("/swagger/index.html");
        var policy = page.Headers.GetValues("Content-Security-Policy").Single();
        var scripts = policy.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src", StringComparison.Ordinal));

        Assert.Equal("script-src 'self'", scripts);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("default-src 'none'", policy);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());

        // The rest of the API keeps the strict policy: only /swagger is relaxed, and only as far as the page needs.
        var other = await factory.Admin().GetAsync("/api/auth/me");
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", other.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task The_page_runs_no_inline_script_and_builds_no_script_from_text()
    {
        var client = factory.Admin();
        var html = await (await client.GetAsync("/swagger/index.html")).Content.ReadAsStringAsync();
        var bootstrap = await (await client.GetAsync("/swagger/index.js")).Content.ReadAsStringAsync();

        // Every script tag in the page names a file, and the bootstrap has no interceptor to compile with new Function (the policy would refuse it).
        Assert.DoesNotContain(System.Text.RegularExpressions.Regex.Matches(html, "<script(?<attrs>[^>]*)>").Select(m => m.Groups["attrs"].Value), a => !a.Contains("src=", StringComparison.Ordinal));
        Assert.Contains("JSON.parse('{}')", bootstrap);
    }

    [Fact]
    public async Task The_document_describes_exactly_the_routes_the_service_maps()
    {
        using var document = await ReadDocumentAsync();
        var described = Operations(document).Select(o => $"{o.Method} {Plain(o.Path)}").ToHashSet();

        var mapped = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {Plain("/" + e.RoutePattern.RawText!.Trim('/'))}"))
            .ToHashSet();

        Assert.Empty(mapped.Except(described));
        Assert.Empty(described.Except(mapped));
    }

    [Fact]
    public async Task Every_route_has_its_own_name_a_written_summary_and_a_group()
    {
        using var document = await ReadDocumentAsync();
        var operations = Operations(document);

        var ids = operations.Select(o => o.Operation.GetProperty("operationId").GetString()!).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        foreach (var (method, path, operation) in operations)
        {
            var id = operation.GetProperty("operationId").GetString()!;
            var name = method == "HEAD" ? id[..^"Head".Length] : id;
            Assert.True(RouteDescriptions.ByName.TryGetValue(name, out var written), $"{method} {path} ({id}) has no entry in RouteDescriptions");
            Assert.StartsWith(written!.Summary, operation.GetProperty("summary").GetString());
            Assert.Contains(written.Detail, operation.GetProperty("description").GetString());
            Assert.Single(operation.GetProperty("tags").EnumerateArray());

            // Every written status is listed, and a success the handler does not give (the reflection's default 200) is not.
            var listed = operation.GetProperty("responses").EnumerateObject().Select(r => r.Name).ToHashSet();
            Assert.All(written.Statuses.Select(s => s.Status.ToString()), status => Assert.Contains(status, listed));
            Assert.True(written.Statuses.Any(s => s.Status == 200) == listed.Contains("200"), $"{method} {path}: a 200 that is not real");
        }

        // And nothing written describes a route that is gone (the reset exists only when switched on).
        var named = operations.Select(o => o.Operation.GetProperty("operationId").GetString()!.Replace("Head", "")).ToHashSet();
        Assert.Empty(RouteDescriptions.ByName.Keys.Except(named).Except(["ResetAllData"]));
        Assert.All(document.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()!),
            tag => Assert.Contains(operations, o => o.Operation.GetProperty("tags")[0].GetString() == tag));
    }

    [Fact]
    public async Task Each_route_names_the_callers_the_role_matrix_proves_can_use_it()
    {
        using var document = await ReadDocumentAsync();
        var operations = Operations(document).ToDictionary(o => $"{o.Method} {o.Path}", o => o.Operation.GetProperty("description").GetString()!);
        var matrix = Lateral.Cms.Tests.Auth.RoleMatrixTests.Matrix().GroupBy(row => Plain((string)row[0]));

        Assert.NotEmpty(matrix);
        foreach (var route in matrix)
        {
            Assert.True(operations.TryGetValue(route.Key, out var text), $"{route.Key} is not in the document");
            var allowed = route.Where(r => (HttpStatusCode)r[2] is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                .Select(r => (string)r[1]).Order().ToList();

            var claimed = text!.Split("**Who may call:** ")[1].TrimEnd() switch
            {
                "the CMS service account only." => new[] { "cms" },
                "a signed-in user or admin." => ["admin", "user"],
                "an admin only." => ["admin"],
                "any signed-in caller." => ["admin", "cms", "user"],
                "the CMS service account or an admin." => ["admin", "cms"],
                "anyone, no sign-in needed." => ["admin", "anonymous", "cms", "user"],
                var other => throw new InvalidOperationException($"{route.Key}: unrecognised wording '{other}'"),
            };

            Assert.True(claimed.SequenceEqual(allowed), $"{route.Key}: the document says {string.Join(",", claimed)}, the matrix proves {string.Join(",", allowed)}");
        }
    }

    [Fact]
    public async Task Routes_that_need_a_caller_say_so_and_say_what_a_refusal_means()
    {
        using var document = await ReadDocumentAsync();

        foreach (var (method, path, operation) in Operations(document))
        {
            var responses = operation.GetProperty("responses");
            var anonymous = operation.GetProperty("description").GetString()!.Contains("anyone, no sign-in needed");
            var secured = operation.TryGetProperty("security", out var security) && security.GetArrayLength() == 1;
            var refusals = responses.TryGetProperty("401", out _) && responses.TryGetProperty("403", out _);
            Assert.True(secured == !anonymous, $"{method} {path}: security");
            // Sign-in is open to anyone and still says what its own 401 and 403 mean; every other open route has none to explain.
            Assert.True(!anonymous || path == "/api/auth/login" || !refusals, $"{method} {path}: an open route lists refusals");
            Assert.True(anonymous || refusals, $"{method} {path}: 401 and 403");
        }

        Assert.Equal("http", document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Basic").GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_example_event_batch_in_the_document_is_one_the_service_accepts()
    {
        using var document = await ReadDocumentAsync();
        var post = Operations(document).Single(o => o.Method == "POST" && o.Path == "/cms/events").Operation;
        var example = post.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example");

        // The example carries a date in the future of the test clock; move the clock to it, as time would.
        factory.Time.Advance(new DateTimeOffset(2026, 11, 8, 9, 1, 0, TimeSpan.Zero) - factory.Time.GetUtcNow());
        var response = await factory.Cms().PostAsync("/cms/events", new StringContent(example.GetRawText(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task The_upload_routes_say_their_body_is_the_raw_bytes_and_name_the_checksum_header()
    {
        using var document = await ReadDocumentAsync();

        foreach (var route in new[] { ("PUT", "/cms/assets/{key}"), ("PUT", "/cms/assets/uploads/{uploadId}/parts/{partNumber}") })
        {
            var operation = Operations(document).Single(o => o.Method == route.Item1 && o.Path == route.Item2).Operation;
            Assert.Equal("binary", operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/octet-stream").GetProperty("schema").GetProperty("format").GetString());
            Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "X-Content-SHA256" && p.GetProperty("in").GetString() == "header");
        }
    }

    [Fact]
    public async Task The_document_holds_no_secret_and_no_value_from_the_running_service()
    {
        var text = await (await factory.Admin().GetAsync(Document)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(CmsFactory.CmsPassword, text);
        Assert.DoesNotContain(CmsFactory.AdminPassword, text);
        Assert.DoesNotContain(CmsFactory.UserPassword, text);
        Assert.DoesNotContain(factory.DatabasePath, text);
    }
}

/// <summary>Switched off in configuration: neither the document nor the page exists, whoever asks.</summary>
[Trait("req", "DX-03")]
public class ApiDocsSwitchedOffTests : IClassFixture<ApiDocsSwitchedOffTests.OffFactory>
{
    private readonly OffFactory _factory;

    public ApiDocsSwitchedOffTests(OffFactory factory) => _factory = factory;

    public sealed class OffFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("ApiDocs:Enabled", "false")];
    }

    [Theory]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/index.html")]
    public async Task It_answers_not_found_even_to_an_admin_and_the_rest_of_the_service_is_unchanged(string path)
    {
        var admin = _factory.Admin();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/health")).StatusCode);
    }
}
