using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Assets;

/// <summary>Getting bytes out: S3-like ranged, resumable, parallel reads of a clean asset, served so it can only ever be a download.</summary>
[Trait("req", "BL-03")]
public class DownloadTests(AssetFactory factory) : IClassFixture<AssetFactory>
{
    private const int Size = 300_000;

    private static byte[] Content => AssetFactory.Png(Size, 7);

    // Clean, and named by a published record, so the user in these tests may fetch it.
    private async Task<Guid> CleanAsync(byte[]? bytes = null, string key = "download.png", string declared = "application/octet-stream") =>
        await factory.CleanAssetAsync(bytes ?? Content, key, declared);

    [Fact]
    public async Task A_user_gets_a_file_only_while_a_record_they_can_see_names_it()
    {
        var id = await factory.CleanAssetAsync(Content, "linked.png", "image/png", show: false);

        // Clean but not named by anything: a user is told it does not exist; an admin may fetch it.
        Assert.Equal(HttpStatusCode.NotFound, (await factory.User().GetAsync($"/api/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.Admin().GetAsync($"/api/assets/{id}")).StatusCode);

        var record = await factory.ShowAsync(id);
        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetAsync($"/api/assets/{id}")).StatusCode);

        // Taken down by an admin: the record and its file leave together.
        Assert.True((await factory.Admin().PostAsync($"/api/entities/{record}/disable", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.User().GetAsync($"/api/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.Admin().GetAsync($"/api/assets/{id}")).StatusCode);
        Assert.True((await factory.Admin().PostAsync($"/api/entities/{record}/enable", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetAsync($"/api/assets/{id}")).StatusCode);

        // Unpublished by the CMS, then deleted: still gone for the user.
        using var unpublished = await factory.Cms().PostOkAsync(Evt.UnPublish(record, 2, 2));
        await factory.DrainAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.User().GetAsync($"/api/assets/{id}")).StatusCode);
    }

    private static HttpRequestMessage Get(Guid id, Action<HttpRequestMessage>? configure = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"/api/assets/{id}");
        configure?.Invoke(request);
        return request;
    }

    // ---- headers ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_is_an_attachment_typed_by_what_the_bytes_are_never_by_what_the_sender_claimed()
    {
        var id = await CleanAsync(key: "my photo.png", declared: "text/plain");

        var response = await factory.User().GetAsync($"/api/assets/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType); // detected, not the claimed text/plain
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("my photo.png", response.Content.Headers.ContentDisposition.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName?.Trim('"'));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal($"\"{AssetFactory.Sha(Content)}\"", response.Headers.ETag!.Tag);
        Assert.Equal(Size, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Users_and_admins_can_download_but_the_cms_account_cannot()
    {
        var id = await CleanAsync();

        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetAsync($"/api/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.Admin().GetAsync($"/api/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Cms().GetAsync($"/api/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/assets/{id}")).StatusCode);
    }

    // ---- ranges -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=1000-1999", 1000, 1999)]
    [InlineData("bytes=299900-", 299900, 299_999)]
    [InlineData("bytes=-50", 299_950, 299_999)]
    public async Task A_byte_range_returns_exactly_those_bytes_with_206_and_a_content_range(string range, long from, long to)
    {
        var id = await CleanAsync();

        var response = await factory.User().SendAsync(Get(id, r => r.Headers.TryAddWithoutValidation("Range", range)));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(from, to, Size), response.Content.Headers.ContentRange);
        Assert.Equal(Content[(int)from..(int)(to + 1)], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_range_outside_the_object_is_416_and_says_how_big_the_object_is()
    {
        var id = await CleanAsync();

        var response = await factory.User().SendAsync(Get(id, r => r.Headers.TryAddWithoutValidation("Range", $"bytes={Size + 10}-{Size + 20}")));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal(Size, response.Content.Headers.ContentRange?.Length);
    }

    [Fact]
    public async Task The_etag_makes_repeat_downloads_free_and_if_range_makes_resuming_safe()
    {
        var id = await CleanAsync();
        var etag = (await factory.User().GetAsync($"/api/assets/{id}")).Headers.ETag!;

        var notModified = await factory.User().SendAsync(Get(id, r => r.Headers.IfNoneMatch.Add(etag)));
        var resumed = await factory.User().SendAsync(Get(id, r =>
        {
            r.Headers.TryAddWithoutValidation("Range", "bytes=10-19");
            r.Headers.IfRange = new RangeConditionHeaderValue(etag);
        }));
        var changed = await factory.User().SendAsync(Get(id, r =>
        {
            r.Headers.TryAddWithoutValidation("Range", "bytes=10-19");
            r.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"something-else\""));
        }));

        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode); // the object is not the one the range was for: send all of it
        Assert.Equal(Size, (await changed.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Head_returns_the_headers_and_no_body()
    {
        var id = await CleanAsync();

        var response = await factory.User().SendAsync(Get(id, method: HttpMethod.Head));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Size, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    // ---- parallel ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Eight_parallel_ranged_downloads_reassemble_to_exactly_the_original_bytes()
    {
        var bytes = AssetFactory.Png(2_000_000, 8);
        var id = await CleanAsync(bytes, "parallel.png");
        const int Parts = 8;
        var chunk = (bytes.Length + Parts - 1) / Parts;

        var pieces = await Task.WhenAll(Enumerable.Range(0, Parts).Select(async i =>
        {
            var from = i * chunk;
            var to = Math.Min(bytes.Length, from + chunk) - 1;
            var response = await factory.User().SendAsync(Get(id, r => r.Headers.TryAddWithoutValidation("Range", $"bytes={from}-{to}")));
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync();
        }));

        var reassembled = pieces.SelectMany(p => p).ToArray();
        Assert.Equal(AssetFactory.Sha(bytes), AssetFactory.Sha(reassembled));
    }

    [Fact]
    public async Task Many_callers_download_the_same_asset_at_once_and_all_get_the_whole_thing()
    {
        var id = await CleanAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => factory.User().GetByteArrayAsync($"/api/assets/{id}")));

        Assert.All(results, r => Assert.Equal(Content, r));
    }

    // ---- the concurrency cap ---------------------------------------------------------------------------------

    public sealed class OneAtATimeFactory : AssetFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> MoreSettings => [new("Assets:MaxConcurrentDownloadsPerCaller", "1")];
    }

    [Fact]
    [Trait("req", "BL-05")]
    public async Task One_caller_cannot_hold_more_downloads_open_than_the_cap_allows_and_others_are_unaffected()
    {
        using var capped = new OneAtATimeFactory();
        var id = await capped.CleanAssetAsync(AssetFactory.Png(3_000_000, 9), "big.png");
        var user = capped.User();

        // The first download is started but its body is not read, so it stays open and holds the caller's only permit.
        using var first = await user.SendAsync(Get(id), HttpCompletionOption.ResponseHeadersRead);
        var second = await user.GetAsync($"/api/assets/{id}");
        var otherCaller = await capped.Admin().GetAsync($"/api/assets/{id}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, otherCaller.StatusCode);

        // Finishing the first frees the permit.
        await first.Content.CopyToAsync(Stream.Null);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"/api/assets/{id}")).StatusCode);
    }
}

/// <summary>A large asset is streamed, not loaded: serving it allocates a small fraction of its size.</summary>
public sealed class LargeAssetFactory : AssetFactory
{
    protected override int SimpleLimit => 1_048_576;

    protected override int PartSize => 8 * 1024 * 1024;

    protected override IEnumerable<KeyValuePair<string, string?>> MoreSettings =>
    [
        new("Assets:MaxSimpleBytes", "1048576"),
        new("Assets:MinPartBytes", "1048576"),
        new("Assets:MaxPartBytes", "16777216"),
        new("Assets:MaxObjectBytes", "268435456"),
        new("Assets:MaxBytes", "536870912"),
        // The base factory scans against a 400 ms limit so the timeout path is quick to test; reading 64 MiB takes longer than that on a busy disk.
        new("Assets:ScanTimeout", "00:00:30"),
    ];
}

[CollectionDefinition("serial-memory", DisableParallelization = true)]
public sealed class SerialMemoryCollection;

[Collection("serial-memory")]
[Trait("req", "BL-08")]
public class LargeAssetTests
{
    [Fact]
    public async Task A_64_MiB_asset_is_uploaded_in_parallel_parts_scanned_and_downloaded_without_holding_it_in_memory()
    {
        using var factory = new LargeAssetFactory();
        const int Total = 64 * 1024 * 1024;
        var bytes = new byte[Total];
        new Random(2024).NextBytes(bytes);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        var expected = AssetFactory.Sha(bytes);

        var (completed, _) = await factory.MultipartAsync(bytes, 8 * 1024 * 1024, "huge.png", "image/png");
        Assert.Equal(HttpStatusCode.Accepted, completed.StatusCode);
        using var body = await completed.ReadJsonAsync();
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(expected, body.RootElement.GetProperty("sha256").GetString());
        await factory.ScanAllAsync();
        Assert.Equal("clean", await factory.StateAsync(id));
        await factory.ShowAsync(id);

        // Read the whole body through a small buffer, hashing as it goes, and look at what the process allocated meanwhile.
        bytes = [];
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        using var response = await factory.User().GetAsync($"/api/assets/{id}", HttpCompletionOption.ResponseHeadersRead);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long read = 0;
        await using (var stream = await response.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                read += n;
            }
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal(Total, read);
        Assert.Equal(expected, Convert.ToHexStringLower(hash.GetHashAndReset()));
        Assert.True(allocated < Total / 4, $"serving {Total} bytes allocated {allocated} bytes: it is being buffered");
    }
}
