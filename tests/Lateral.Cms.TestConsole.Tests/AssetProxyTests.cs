using System.Net;
using System.Net.Http.Headers;

namespace Lateral.Cms.TestConsole.Tests;

/// <summary>What the console passes between the page and the asset routes: ranges and checksums in, partial-content headers out, nothing else.</summary>
public class AssetProxyTests : IClassFixture<ConsoleFactory>
{
    private readonly ConsoleFactory _factory;

    public AssetProxyTests(ConsoleFactory factory)
    {
        _factory = factory;
        _factory.Cms.Reset();
    }

    [Fact]
    public async Task A_ranged_download_carries_the_range_headers_to_the_cms_and_the_partial_content_headers_back()
    {
        _factory.Cms.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, 13, 100);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "a.png" };
            response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.Add("X-Internal-Detail", "must not leak");
            return response;
        };
        var client = _factory.Page();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/cms/api/assets/6f0c8f64-0000-4000-8000-000000000001");
        request.Headers.Add("X-Console-Act-As", "user");
        request.Headers.TryAddWithoutValidation("Range", "bytes=10-13");
        request.Headers.TryAddWithoutValidation("If-Range", "\"abc\"");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"zzz\"");
        request.Headers.TryAddWithoutValidation("Cookie", "session=secret");
        request.Headers.TryAddWithoutValidation("X-Evil", "1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 10-13/100", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal("\"abc\"", response.Headers.ETag?.Tag);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString());
        Assert.False(response.Headers.Contains("X-Internal-Detail"));

        var sent = Assert.Single(_factory.Cms.Requests);
        Assert.Equal(ConsoleFactory.Basic(ConsoleFactory.UserName, ConsoleFactory.UserPassword), sent.Authorization);
        Assert.Equal("/api/assets/6f0c8f64-0000-4000-8000-000000000001", sent.Uri.AbsolutePath);
    }

    [Fact]
    public async Task An_upload_part_streams_its_body_and_its_checksum_header_to_the_cms()
    {
        _factory.Cms.Respond = _ => ConsoleFactoryResponse.Json(HttpStatusCode.OK, "{\"partNumber\":1}");
        var client = _factory.Page();
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/cms/cms/assets/uploads/6f0c8f64-0000-4000-8000-000000000001/parts/1")
        {
            Content = new ByteArrayContent(new byte[2048]),
        };
        request.Headers.Add("X-Console-Act-As", "cms");
        request.Headers.TryAddWithoutValidation("X-Content-SHA256", new string('a', 64));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_factory.Cms.Requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal(2048, sent.ContentLength);
        Assert.Equal(ConsoleFactory.Basic(ConsoleFactory.CmsUser, ConsoleFactory.CmsPassword), sent.Authorization);
    }

    [Fact]
    public async Task Paths_outside_the_cms_routes_are_still_refused_for_assets_too()
    {
        var client = _factory.Page();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/cms/cms/assets/../secrets");
        request.Headers.Add("X-Console-Act-As", "user");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }
}

internal static class ConsoleFactoryResponse
{
    public static HttpResponseMessage Json(HttpStatusCode status, string json) => FakeCms.Json(status, json);
}
