using System.Net;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Assets;

/// <summary>A small store, so a few assets fill it.</summary>
public sealed class SmallStoreFactory : AssetFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> MoreSettings =>
    [
        new("Assets:MaxSimpleBytes", "50000"),
        new("Assets:MaxObjectBytes", "60000"),
        new("Assets:MaxBytes", "100000"),
        new("Assets:UploadTtl", "01:00:00"),
    ];
}

/// <summary>What the store promises about space, expiry, orphans, deletion and shared content.</summary>
public class HousekeepingTests(SmallStoreFactory factory) : IClassFixture<SmallStoreFactory>
{
    private static string Hex(Guid id) => id.ToString().ToUpperInvariant();

    // ---- quota --------------------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "BL-05")]
    public async Task Past_the_store_cap_uploads_get_429_with_retry_after_and_nothing_is_stored()
    {
        await factory.SettleScansAsync();
        var used = factory.Count("SELECT COALESCE(SUM(Size), 0) FROM Assets WHERE State <> 'rejected'");
        var fill = (int)(100_000 - used - 10_000); // leave 10 KB of room
        var bytes = new byte[Math.Min(fill, 50_000)];
        var ids = new List<Guid>();
        var remaining = fill;
        while (remaining > 0)
        {
            var piece = AssetFactory.Png(Math.Min(remaining, 50_000), remaining);
            var (response, body) = await factory.PutAssetAsync(piece, $"fill-{ids.Count}.png", "image/png");
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            ids.Add(body.RootElement.GetProperty("id").GetGuid());
            remaining -= piece.Length;
        }

        var filesBefore = factory.FilesOnDisk().Length;
        var rowsBefore = factory.Count("SELECT COUNT(*) FROM Assets");

        var simple = await factory.Cms().PutAsync("/cms/assets/too-much", AssetFactory.Body(AssetFactory.Png(20_000, 3)));
        var multipart = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "too-much", size = 20_000 }));

        Assert.Equal(HttpStatusCode.TooManyRequests, simple.StatusCode);
        Assert.NotNull(simple.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, multipart.StatusCode);
        using (var problem = await simple.ReadJsonAsync())
        {
            Assert.Equal("assets_full", problem.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(filesBefore, factory.FilesOnDisk().Length);
        Assert.Equal(rowsBefore, factory.Count("SELECT COUNT(*) FROM Assets"));

        // Freeing space (by deleting) lets the same upload through.
        foreach (var id in ids)
        {
            await factory.Admin().DeleteAsync($"/api/assets/{id}");
        }

        Assert.Equal(HttpStatusCode.Accepted, (await factory.Cms().PutAsync("/cms/assets/now-fits", AssetFactory.Body(AssetFactory.Png(20_000, 4)))).StatusCode);
        await factory.SettleScansAsync();
    }

    [Fact]
    [Trait("req", "BL-05")]
    public async Task Parts_in_flight_count_against_the_cap_so_an_upload_that_would_not_fit_is_refused_part_by_part()
    {
        await factory.SettleScansAsync();
        var used = factory.Count("SELECT COALESCE(SUM(Size), 0) FROM Assets WHERE State <> 'rejected'")
                   + factory.Count("SELECT COALESCE(SUM(Size), 0) FROM UploadParts");
        var room = 100_000 - used;
        var size = (int)Math.Min(50_000, room - 100); // declared size fits
        var initiate = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "parts-cap", size }));
        Assert.Equal(HttpStatusCode.Created, initiate.StatusCode);
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();

        // Meanwhile another upload takes the room.
        var squatter = await factory.Cms().PutAsync("/cms/assets/squatter", AssetFactory.Body(AssetFactory.Png((int)Math.Min(50_000, room - 50), 6)));
        Assert.Equal(HttpStatusCode.Accepted, squatter.StatusCode);
        var part = await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(new byte[Math.Min(size, 60_000)]));

        Assert.Equal(HttpStatusCode.TooManyRequests, part.StatusCode);
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM UploadParts WHERE UploadId = '{Hex(uploadId)}'"));
        await factory.Cms().DeleteAsync($"/cms/assets/uploads/{uploadId}");
        await factory.SettleScansAsync();
    }

    // ---- expiry and orphans ----------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "BL-06")]
    public async Task An_unfinished_upload_expires_and_the_sweeper_removes_its_parts_from_disk_and_database()
    {
        var initiate = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "stale.bin", size = 2_000 }));
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(new byte[1_500]))).StatusCode);
        Assert.Contains(factory.FilesOnDisk(), f => f.Contains(uploadId.ToString("N")));

        factory.Time.Advance(TimeSpan.FromHours(2)); // past the one-hour TTL

        // An expired upload can no longer be added to or completed, even before the sweeper has run.
        var late = await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/2", AssetFactory.Body(new byte[500]));
        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);

        var report = await factory.SweepAsync();

        Assert.True(report.ExpiredUploads >= 1);
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM Uploads WHERE UploadId = '{Hex(uploadId)}'"));
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM UploadParts WHERE UploadId = '{Hex(uploadId)}'"));
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.Contains(uploadId.ToString("N")));
    }

    [Fact]
    [Trait("req", "BL-06")]
    public async Task The_sweeper_removes_files_that_no_row_accounts_for_and_leaves_everything_that_one_does()
    {
        await factory.SettleScansAsync();
        var clean = await factory.CleanAssetAsync(AssetFactory.Png(800, 13), "kept.png", "image/png");
        var waiting = await factory.Cms().PutAsync("/cms/assets/waiting", AssetFactory.Body(AssetFactory.Png(900, 12), "image/png"));
        using var waitingBody = await waiting.ReadJsonAsync();
        var waitingId = waitingBody.RootElement.GetProperty("id").GetGuid();

        // Strays: a quarantined file with no asset, a blob nothing references, a part folder with no upload, a half-written temp file.
        var root = factory.AssetsPath;
        var strayQuarantine = Path.Combine(root, "quarantine", Guid.NewGuid().ToString("N"));
        var strayBlob = Path.Combine(root, "blobs", "ab", "cd", new string('a', 64));
        var strayPartDir = Path.Combine(root, "parts", Guid.NewGuid().ToString("N"));
        var strayTemp = Path.Combine(root, "quarantine", Guid.NewGuid().ToString("N") + ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(strayBlob)!);
        Directory.CreateDirectory(strayPartDir);
        File.WriteAllText(strayQuarantine, "stray");
        File.WriteAllText(strayBlob, "stray");
        File.WriteAllText(Path.Combine(strayPartDir, "1"), "stray");
        File.WriteAllText(strayTemp, "stray");

        var report = await factory.SweepAsync();

        Assert.True(report.OrphanFiles >= 4, $"removed {report.OrphanFiles}");
        Assert.False(File.Exists(strayQuarantine));
        Assert.False(File.Exists(strayBlob));
        Assert.False(Directory.Exists(strayPartDir));
        Assert.False(File.Exists(strayTemp));
        Assert.Contains(factory.FilesOnDisk(), f => f.EndsWith(waitingId.ToString("N"))); // still waiting to be scanned
        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetAsync($"/api/assets/{clean}")).StatusCode);
        await factory.SettleScansAsync();
    }

    [Fact]
    [Trait("req", "BL-06")]
    public async Task The_sweeper_leaves_young_files_alone_so_it_cannot_take_one_that_is_still_being_registered()
    {
        using var graceful = new GraceFactory();
        _ = graceful.Services;
        var young = Path.Combine(graceful.AssetsPath, "quarantine", Guid.NewGuid().ToString("N"));
        File.WriteAllText(young, "just written");

        await graceful.SweepAsync();

        Assert.True(File.Exists(young));
    }

    public sealed class GraceFactory : AssetFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> MoreSettings => [new("Assets:SweepGrace", "01:00:00")];
    }

    // ---- deletion and shared content ---------------------------------------------------------------------------

    [Fact]
    [Trait("req", "BL-07")]
    public async Task An_admin_delete_removes_the_row_and_wipes_the_bytes_and_a_user_cannot()
    {
        await factory.SettleScansAsync();
        var bytes = AssetFactory.Png(1_200, 91);
        var id = await factory.CleanAssetAsync(bytes, "delete-me.png", "image/png");
        var blob = factory.FilesOnDisk().Single(f => f.EndsWith(AssetFactory.Sha(bytes)));

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.User().DeleteAsync($"/api/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Cms().DeleteAsync($"/api/assets/{id}")).StatusCode);
        Assert.True(File.Exists(blob));

        Assert.Equal(HttpStatusCode.NoContent, (await factory.Admin().DeleteAsync($"/api/assets/{id}")).StatusCode);

        Assert.False(File.Exists(blob));
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM Assets WHERE Id = '{Hex(id)}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Admin().DeleteAsync($"/api/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Admin().GetAsync($"/api/assets/{id}")).StatusCode);
    }

    [Fact]
    [Trait("req", "BL-07")]
    public async Task Identical_content_is_stored_once_and_survives_until_the_last_asset_that_uses_it_is_deleted()
    {
        await factory.SettleScansAsync();
        var bytes = AssetFactory.Png(2_000, 92);
        var sha = AssetFactory.Sha(bytes);
        var first = await factory.CleanAssetAsync(bytes, "one.png", "image/png");
        var second = await factory.CleanAssetAsync(bytes, "two.png", "image/png");

        Assert.NotEqual(first, second);
        Assert.Single(factory.FilesOnDisk(), f => f.EndsWith(sha));
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.EndsWith(first.ToString("N")) || f.EndsWith(second.ToString("N")));

        await factory.Admin().DeleteAsync($"/api/assets/{first}");
        Assert.Single(factory.FilesOnDisk(), f => f.EndsWith(sha)); // still needed by the second
        Assert.Equal(bytes, await factory.User().GetByteArrayAsync($"/api/assets/{second}"));

        await factory.Admin().DeleteAsync($"/api/assets/{second}");
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.EndsWith(sha));
    }

    [Fact]
    [Trait("req", "BL-07")]
    public async Task Deleted_content_is_overwritten_with_zeros_even_while_someone_has_the_file_open()
    {
        var path = Path.Combine(factory.AssetsPath, "quarantine", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)0xAB, 300_000).ToArray());
        var store = (Lateral.Cms.Assets.IAssetStore)factory.Services.GetService(typeof(Lateral.Cms.Assets.IAssetStore))!;

        bool removed;
        var seen = new byte[300_000];
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            removed = await store.WipeAsync(path, CancellationToken.None);
            reader.Position = 0;
            _ = await reader.ReadAtLeastAsync(seen, seen.Length, throwOnEndOfStream: false);
        }

        // Whatever the platform does about deleting an open file, what the open handle can still read is zeros, not the content.
        Assert.All(seen, b => Assert.Equal(0, b));
        if (!removed)
        {
            Assert.True(File.Exists(path)); // reported honestly, not claimed as done
        }

        Assert.True(await store.WipeAsync(path, CancellationToken.None));
        Assert.False(File.Exists(path));
    }
}
