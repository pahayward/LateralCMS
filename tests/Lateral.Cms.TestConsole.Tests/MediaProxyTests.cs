using System.Net;
using System.Net.Http.Headers;

namespace Lateral.Cms.TestConsole.Tests;

/// <summary>
/// The pictures in the photo library. A browser's image tag cannot send the console's header or an Authorization header, so the
/// console fetches the asset as the reader account on its behalf, for that one route only.
/// </summary>
public class MediaProxyTests : IClassFixture<ConsoleFactory>
{
    private const string Id = "6f0c8f64-0000-4000-8000-000000000001";
    private readonly ConsoleFactory _factory;

    public MediaProxyTests(ConsoleFactory factory)
    {
        _factory = factory;
        _factory.Cms.Reset();
    }

    private static HttpResponseMessage Picture(HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([1, 2, 3, 4]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "holiday.jpg" };
        response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
        response.Headers.AcceptRanges.Add("bytes");
        response.Headers.Add("X-Internal-Detail", "must not leak");
        return response;
    }

    [Fact]
    public async Task A_picture_loads_without_the_console_header_and_is_read_as_the_reader_account()
    {
        _factory.Cms.Respond = _ => Picture();

        // An <img> sends neither the X-Console-Request header nor a chosen account: this is just a GET.
        var response = await _factory.CreateClient().GetAsync($"/media/{Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("\"abc\"", response.Headers.ETag?.Tag);
        Assert.False(response.Headers.Contains("X-Internal-Detail"));

        var sent = Assert.Single(_factory.Cms.Requests);
        Assert.Equal(ConsoleFactory.Basic(ConsoleFactory.UserName, ConsoleFactory.UserPassword), sent.Authorization);
        Assert.Equal($"/api/assets/{Id}", sent.Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_picture_is_cached_for_good_because_an_asset_never_changes()
    {
        _factory.Cms.Respond = _ => Picture();

        var response = await _factory.CreateClient().GetAsync($"/media/{Id}");

        var cache = response.Headers.CacheControl;
        Assert.True(cache?.Private);
        Assert.Equal(TimeSpan.FromDays(365), cache?.MaxAge);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task It_is_shown_in_the_page_unless_the_page_asks_for_a_download()
    {
        _factory.Cms.Respond = _ => Picture();
        var client = _factory.CreateClient();

        var shown = await client.GetAsync($"/media/{Id}");
        var saved = await client.GetAsync($"/media/{Id}?download=1");

        Assert.Null(shown.Content.Headers.ContentDisposition);
        Assert.Equal("attachment", saved.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("holiday.jpg", saved.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task A_range_is_passed_on_and_the_partial_answer_comes_back_for_parallel_downloads()
    {
        string? range = null;
        _factory.Cms.Respond = _ =>
        {
            var response = Picture(HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, 13, 100);
            return response;
        };
        var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{Id}");
        request.Headers.TryAddWithoutValidation("Range", "bytes=10-13");
        request.Headers.TryAddWithoutValidation("Cookie", "session=secret");
        request.Headers.TryAddWithoutValidation("X-Evil", "1");

        var response = await _factory.CreateClient().SendAsync(request);
        range = response.Content.Headers.ContentRange?.ToString();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 10-13/100", range);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_picture_the_cms_does_not_have_is_not_found_and_is_never_cached()
    {
        _factory.Cms.Respond = _ => FakeCms.Json(HttpStatusCode.NotFound, "{}");

        var response = await _factory.CreateClient().GetAsync($"/media/{Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_refused_download_because_too_many_are_running_is_asked_for_again()
    {
        var attempts = 0;
        _factory.Cms.Respond = _ => Interlocked.Increment(ref attempts) < 3 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Picture();

        var response = await _factory.CreateClient().GetAsync($"/media/{Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, _factory.Cms.Requests.Count);
    }

    [Fact]
    public async Task Only_asset_ids_can_be_asked_for_and_only_to_read_so_nothing_else_is_reachable_this_way()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/media/not-an-id")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/media/..%2f..%2fcms%2fevents")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync($"/media/{Id}", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync($"/media/{Id}")).StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    [Fact]
    public async Task A_request_from_another_site_or_to_another_host_name_is_refused()
    {
        _factory.Cms.Respond = _ => Picture();
        var client = _factory.CreateClient();

        var crossSite = new HttpRequestMessage(HttpMethod.Get, $"/media/{Id}");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        var rebinding = new HttpRequestMessage(HttpMethod.Get, $"/media/{Id}");
        rebinding.Headers.Host = "evil.example";

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode);

        // Refused either by the host filter (400, which only lets loopback names in) or by the console's own check (403).
        Assert.Contains((await client.SendAsync(rebinding)).StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden });
        Assert.Empty(_factory.Cms.Requests);
    }

    [Fact]
    public async Task An_unreachable_cms_is_a_bad_gateway_not_a_crash()
    {
        _factory.Cms.Throw = new HttpRequestException("down");

        var response = await _factory.CreateClient().GetAsync($"/media/{Id}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
