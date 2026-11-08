using System.Net;
using System.Text;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Assets;

/// <summary>
/// Deleting a record deletes everything of it, on disk as well as in the tables: its files (overwritten, then removed, and the
/// folders they leave), what the ledger kept of its content, and the database's own earlier copies. The proof is a search of every
/// byte the service keeps for text that was in the record and in the file.
/// </summary>
[Trait("req", "BL-09")]
public class ErasureTests(AssetFactory factory) : IClassFixture<AssetFactory>
{
    private static byte[] FileWithMarker(string marker, int seed) =>
        [.. AssetFactory.Png(2_000, seed), .. Encoding.UTF8.GetBytes(marker), .. AssetFactory.Png(2_000, seed + 1)[8..]];

    private static bool Holds(string path, string marker)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0;
    }

    /// <summary>Every place the service keeps bytes: the asset folders, the database and its write-ahead log.</summary>
    private bool AnywhereOnDisk(string marker) =>
        factory.FilesOnDisk().Any(f => Holds(f, marker))
        || Holds(factory.DatabasePath, marker)
        || Holds(factory.DatabasePath + "-wal", marker);

    private async Task<(Guid Asset, string Record)> StoredAsync(string fileMarker, string recordMarker, int seed, string? recordId = null)
    {
        var asset = await factory.CleanAssetAsync(FileWithMarker(fileMarker, seed), $"erase-{seed}.png", "image/png", show: false);
        var record = recordId ?? Evt.Id();
        using var _ = await factory.Cms().PostOkAsync(Evt.Publish(record, 1, 1, new { note = recordMarker, file = asset }));
        await factory.DrainAsync();
        return (asset, record);
    }

    [Fact]
    public async Task Deleting_a_record_leaves_no_trace_of_it_or_its_files_anywhere_on_disk()
    {
        var fileMarker = $"FILE-SECRET-{Guid.NewGuid():N}";
        var recordMarker = $"RECORD-SECRET-{Guid.NewGuid():N}";
        var (asset, record) = await StoredAsync(fileMarker, recordMarker, seed: 201);

        // The control: while the record exists, the secrets really are on disk, and the user can read the file.
        Assert.True(AnywhereOnDisk(fileMarker), "the file's bytes should be on disk before the delete");
        Assert.True(AnywhereOnDisk(recordMarker), "the record's text should be in the database before the delete");
        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetAsync($"/api/assets/{asset}")).StatusCode);

        using var deleted = await factory.Cms().PostOkAsync(Evt.Delete(record, 10));
        await factory.DrainAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await factory.User().GetAsync($"/api/entities/{record}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Admin().GetAsync($"/api/assets/{asset}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Cms().GetAsync($"/cms/assets/{asset}")).StatusCode);
        Assert.False(AnywhereOnDisk(fileMarker), "the file's bytes are still on disk");
        Assert.False(AnywhereOnDisk(recordMarker), "the record's text is still on disk");
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.Contains($"{Path.DirectorySeparatorChar}blobs{Path.DirectorySeparatorChar}"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(factory.AssetsPath, "blobs"))); // not even the empty folders of the file's address
        Assert.Empty(factory.Sql($"SELECT 1 FROM AssetLinks WHERE EntityId = '{record}'"));
        Assert.Empty(factory.Sql($"SELECT 1 FROM Events WHERE EntityId = '{record}' AND PayloadChecksum IS NOT NULL")); // no hash of its content either
        Assert.Contains(factory.Sql("SELECT Action FROM AuditLog"), r => (string)r[0]! == "asset.deleted");
    }

    [Fact]
    public async Task A_file_two_records_name_goes_only_with_the_last_of_them()
    {
        var marker = $"SHARED-{Guid.NewGuid():N}";
        var (asset, first) = await StoredAsync(marker, "one", seed: 211);
        var second = Evt.Id();
        using (var _ = await factory.Cms().PostOkAsync(Evt.Publish(second, 1, 1, new { also = asset })))
        {
            await factory.DrainAsync();
        }

        using (var _ = await factory.Cms().PostOkAsync(Evt.Delete(first, 10)))
        {
            await factory.DrainAsync();
        }

        Assert.True(AnywhereOnDisk(marker), "the second record still names the file");
        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetAsync($"/api/assets/{asset}")).StatusCode);

        using (var _ = await factory.Cms().PostOkAsync(Evt.Delete(second, 11)))
        {
            await factory.DrainAsync();
        }

        Assert.False(AnywhereOnDisk(marker), "nothing names the file any more");
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Admin().GetAsync($"/api/assets/{asset}")).StatusCode);
    }

    [Fact]
    public async Task Identical_bytes_in_two_files_stay_until_the_last_file_is_gone()
    {
        var marker = $"TWIN-{Guid.NewGuid():N}";
        var bytes = FileWithMarker(marker, 221);
        var a = await factory.CleanAssetAsync(bytes, "twin-a.png", "image/png", show: false);
        var b = await factory.CleanAssetAsync(bytes, "twin-b.png", "image/png", show: false);
        var recordA = Evt.Id();
        var recordB = Evt.Id();
        using (var _ = await factory.Cms().PostOkAsync(Evt.Publish(recordA, 1, 1, new { file = a }), Evt.Publish(recordB, 1, 1, new { file = b })))
        {
            await factory.DrainAsync();
        }

        using (var _ = await factory.Cms().PostOkAsync(Evt.Delete(recordA, 10)))
        {
            await factory.DrainAsync();
        }

        Assert.True(AnywhereOnDisk(marker), "the other file has the same bytes and still needs them");
        Assert.Equal(bytes, await factory.Admin().GetByteArrayAsync($"/api/assets/{b}"));

        using (var _ = await factory.Cms().PostOkAsync(Evt.Delete(recordB, 11)))
        {
            await factory.DrainAsync();
        }

        Assert.False(AnywhereOnDisk(marker));
    }

    [Fact]
    public async Task Unpublishing_hides_a_records_files_but_keeps_them_because_the_data_is_kept()
    {
        var marker = $"HIDDEN-{Guid.NewGuid():N}";
        var (asset, record) = await StoredAsync(marker, "kept", seed: 231);

        using (var _ = await factory.Cms().PostOkAsync(Evt.UnPublish(record, 2, 5, new { note = "kept", file = asset })))
        {
            await factory.DrainAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await factory.User().GetAsync($"/api/assets/{asset}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.Admin().GetAsync($"/api/assets/{asset}")).StatusCode);
        Assert.True(AnywhereOnDisk(marker));
    }

    [Fact]
    public async Task A_deleted_record_takes_a_file_that_is_still_being_scanned_with_it()
    {
        var marker = $"QUARANTINED-{Guid.NewGuid():N}";
        var (response, body) = await factory.PutAssetAsync(FileWithMarker(marker, 241), "waiting.png", "image/png");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var asset = body.RootElement.GetProperty("id").GetGuid();
        var record = Evt.Id();
        using (var _ = await factory.Cms().PostOkAsync(Evt.Publish(record, 1, 1, new { file = asset })))
        {
            await factory.DrainAsync();
        }

        Assert.True(AnywhereOnDisk(marker), "it is in quarantine");

        using (var _ = await factory.Cms().PostOkAsync(Evt.Delete(record, 10)))
        {
            await factory.DrainAsync();
        }

        Assert.False(AnywhereOnDisk(marker));
        Assert.Empty(factory.Sql($"SELECT 1 FROM Assets WHERE Id = '{asset.ToString().ToUpperInvariant()}'"));
    }

    [Fact]
    public async Task Deleting_a_file_directly_leaves_nothing_on_disk_either()
    {
        var marker = $"DIRECT-{Guid.NewGuid():N}";
        var asset = await factory.CleanAssetAsync(FileWithMarker(marker, 251), "direct.png", "image/png");
        Assert.True(AnywhereOnDisk(marker));

        Assert.Equal(HttpStatusCode.NoContent, (await factory.Admin().DeleteAsync($"/api/assets/{asset}")).StatusCode);

        Assert.False(AnywhereOnDisk(marker));
    }

    [Fact]
    public async Task Parts_of_an_abandoned_upload_are_overwritten_not_just_unlinked()
    {
        var init = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "parts.bin", contentType = "application/octet-stream", size = AssetFactory.MinPart * 2 }));
        using var started = await init.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();
        var data = Enumerable.Range(0, AssetFactory.MinPart).Select(i => (byte)(i % 250 + 1)).ToArray(); // no zero bytes
        Assert.Equal(HttpStatusCode.OK, (await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(data))).StatusCode);

        var partFile = Directory.GetFiles(Path.Combine(factory.AssetsPath, "parts"), "*", SearchOption.AllDirectories).Single();
        using var reader = new FileStream(partFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(data, ReadAll(reader)); // the control: it holds the data

        Assert.Equal(HttpStatusCode.NoContent, (await factory.Cms().DeleteAsync($"/cms/assets/uploads/{uploadId}")).StatusCode);

        reader.Position = 0;
        Assert.All(ReadAll(reader), b => Assert.Equal(0, b)); // whoever still had it open sees zeros, not the data
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
