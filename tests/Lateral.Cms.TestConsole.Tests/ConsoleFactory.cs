using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.TestConsole.Tests;

public sealed record Captured(
    HttpMethod Method, Uri Uri, string? Authorization, string? Body, long? ContentLength, string? ContentType, bool ExpectContinue);

/// <summary>Stands in for the CMS: records what the console sent and answers with whatever the test says.</summary>
public sealed class FakeCms : HttpMessageHandler
{
    private readonly List<Captured> _requests = [];

    public IReadOnlyList<Captured> Requests
    {
        get { lock (_requests) { return [.. _requests]; } }
    }

    public Func<Captured, HttpResponseMessage> Respond { get; set; } = _ => Json(HttpStatusCode.OK, "{}");

    public Exception? Throw { get; set; }

    public void Reset()
    {
        lock (_requests) { _requests.Clear(); }
        Respond = _ => Json(HttpStatusCode.OK, "{}");
        Throw = null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Only read bodies that are known to be small: an oversized probe must not be drained here.
        string? body = null;
        var length = request.Content?.Headers.ContentLength;
        if (request.Content is not null && length is <= 1_000_000)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var captured = new Captured(
            request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body, length,
            request.Content?.Headers.ContentType?.ToString(), request.Headers.ExpectContinue == true);
        lock (_requests) { _requests.Add(captured); }

        return Throw is not null ? throw Throw : Respond(captured);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>The console under test, pointed at <see cref="FakeCms"/> and given known accounts.</summary>
public sealed class ConsoleFactory : WebApplicationFactory<Program>
{
    public const string CmsUser = "cms-account-name";
    public const string CmsPassword = "cms-password-value";
    public const string UserName = "reader@example.test";
    public const string UserPassword = "reader-password-value";

    public FakeCms Cms { get; } = new();

    public static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development, so the CMS's own settings file is never read: only the accounts below exist.
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Console:CmsBaseUrl"] = "http://cms.test",
            ["Console:Accounts:cms:Username"] = CmsUser,
            ["Console:Accounts:cms:Password"] = CmsPassword,
            ["Console:Accounts:user:Username"] = UserName,
            ["Console:Accounts:user:Password"] = UserPassword,
        }));
        builder.ConfigureTestServices(services => services.AddHttpClient("cms").ConfigurePrimaryHttpMessageHandler(() => Cms));
    }

    /// <summary>A client that behaves like the console's own page: same origin, with the request header.</summary>
    public HttpClient Page()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Console-Request", "1");
        return client;
    }
}

public static class ConsoleClientExtensions
{
    public static async Task<JsonDocument> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    public static Task<HttpResponseMessage> ProxyAsync(
        this HttpClient client, string path, string actAs, HttpMethod? method = null, HttpContent? content = null, string? custom = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, "/api/cms/" + path) { Content = content };
        request.Headers.Add("X-Console-Act-As", actAs);
        if (custom is not null)
        {
            request.Headers.Add("X-Console-Custom-Credentials", custom);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> ProbeAsync(this HttpClient client, long bytes, string mode = "declared", bool expectContinue = false) =>
        client.PostAsJsonAsync("/api/probe/oversize", new { bytes, mode, expectContinue });
}
