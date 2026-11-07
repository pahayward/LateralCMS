using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Assets;

/// <summary>
/// The service with small asset limits (so tests need kilobytes, not gigabytes), the mock scanner's failure markers on, a short
/// scan timeout and no grace period for the sweeper. The scan worker is off: a test decides when scanning happens.
/// </summary>
public class AssetFactory : CmsFactory
{
    public const int MinPart = 1024;
    public const int MaxPart = 256 * 1024;
    public const int MaxSimple = 64 * 1024;

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Assets:MaxSimpleBytes", MaxSimple.ToString()),
        new("Assets:MinPartBytes", MinPart.ToString()),
        new("Assets:MaxPartBytes", MaxPart.ToString()),
        new("Assets:MaxObjectBytes", (8 * 1024 * 1024).ToString()),
        new("Assets:MaxBytes", (16 * 1024 * 1024).ToString()),
        new("Assets:ScanRetryBaseDelay", "00:00:10"),
        new("Assets:ScanRetryMaxDelay", "00:01:00"),
        new("Assets:ScanMaxAttempts", "3"),
        new("Assets:ScanTimeout", "00:00:00.400"),
        new("Assets:SweepGrace", "00:00:00"),
        new("Assets:Mock:EnableFailureMarkers", "true"),
        new("Assets:Mock:DetectEicar", "true"),
        ..MoreSettings,
    ];

    protected virtual IEnumerable<KeyValuePair<string, string?>> MoreSettings => [];

    /// <summary>The largest object this factory accepts in a single PUT, and the part size to use above it.</summary>
    protected virtual int SimpleLimit => MaxSimple;

    protected virtual int PartSize => MaxPart;

    /// <summary>Deterministic bytes that start like a PNG, so the scanner sees an image.</summary>
    public static byte[] Png(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static ByteArrayContent Body(byte[] bytes, string contentType = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return content;
    }

    /// <summary>Single PUT as the CMS. Returns the response and its parsed body.</summary>
    public async Task<(HttpResponseMessage Response, JsonDocument Body)> PutAssetAsync(
        byte[] bytes, string key = "file.bin", string contentType = "application/octet-stream", HttpClient? client = null)
    {
        var response = await (client ?? Cms()).PutAsync($"/cms/assets/{Uri.EscapeDataString(key)}", Body(bytes, contentType));
        return (response, await response.ReadJsonAsync());
    }

    /// <summary>Uploads, scans, and returns the asset id once it is clean. Fails the test if it is not.</summary>
    public async Task<Guid> CleanAssetAsync(byte[] bytes, string key = "file.bin", string contentType = "application/octet-stream")
    {
        // Small objects in one PUT; anything bigger the way a real client would: in parts.
        HttpResponseMessage response;
        if (bytes.Length <= SimpleLimit)
        {
            (response, _) = await PutAssetAsync(bytes, key, contentType);
        }
        else
        {
            (response, _) = await MultipartAsync(bytes, PartSize, key, contentType);
        }

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = await response.ReadJsonAsync();
        var id = body.RootElement.GetProperty("id").GetGuid();
        await ScanAllAsync();
        Assert.Equal("clean", await StateAsync(id));
        return id;
    }

    /// <summary>
    /// Lets the clock run on and scans until nothing is waiting any more (everything is cleared, rejected or set aside). Tests that
    /// count scan steps call it first, so assets left by an earlier test in the same class cannot be scanned in their place.
    /// </summary>
    public async Task SettleScansAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            Time.Advance(TimeSpan.FromMinutes(10));
            await ScanAllAsync();
        }
    }

    public async Task<string> StateAsync(Guid id)
    {
        using var status = await StatusAsync(id);
        return status.RootElement.GetProperty("state").GetString()!;
    }

    public async Task<JsonDocument> StatusAsync(Guid id)
    {
        var response = await Cms().GetAsync($"/cms/assets/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    /// <summary>The full multipart flow: initiate, upload the parts concurrently, complete. Returns the completion response.</summary>
    public async Task<(HttpResponseMessage Response, Guid UploadId)> MultipartAsync(byte[] bytes, int partSize, string key = "big.bin", string contentType = "application/octet-stream")
    {
        var initiate = await Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key, contentType, size = bytes.Length }));
        Assert.Equal(HttpStatusCode.Created, initiate.StatusCode);
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();

        var parts = Enumerable.Range(0, (bytes.Length + partSize - 1) / partSize)
            .Select(i => (Number: i + 1, Data: bytes.AsMemory(i * partSize, Math.Min(partSize, bytes.Length - (i * partSize))).ToArray()))
            .ToList();
        var receipts = await Task.WhenAll(parts.Select(async p =>
        {
            var response = await Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/{p.Number}", Body(p.Data));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"part {p.Number}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}{Errors()}");
            return (p.Number, Sha: Sha(p.Data));
        }));

        var completed = await Cms().PostAsync(
            $"/cms/assets/uploads/{uploadId}/complete",
            Evt.Json(new { parts = receipts.OrderBy(r => r.Number).Select(r => new { partNumber = r.Number, sha256 = r.Sha }), sha256 = Sha(bytes) }));
        return (completed, uploadId);
    }

    /// <summary>Every file under this factory's asset folder.</summary>
    public string[] FilesOnDisk() =>
        Directory.Exists(AssetsPath) ? Directory.GetFiles(AssetsPath, "*", SearchOption.AllDirectories) : [];
}
