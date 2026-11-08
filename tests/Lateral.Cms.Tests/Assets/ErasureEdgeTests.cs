using System.Net;
using System.Text;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Assets;

/// <summary>Corners of erasure: a delete and a re-create folded together, and files that a record stops naming.</summary>
[Trait("req", "BL-09")]
public class ErasureEdgeTests(AssetFactory factory) : IClassFixture<AssetFactory>
{
    private static byte[] FileWithMarker(string marker, int seed) =>
        [.. AssetFactory.Png(2_000, seed), .. Encoding.UTF8.GetBytes(marker), .. AssetFactory.Png(2_000, seed + 1)[8..]];

    private bool OnDisk(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        return factory.FilesOnDisk().Any(f =>
        {
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray().AsSpan().IndexOf(needle) >= 0;
        });
    }

    [Fact]
    public async Task A_delete_and_a_later_re_create_in_one_batch_still_erases_the_files_of_the_deleted_record()
    {
        var marker = $"OLDLIFE-{Guid.NewGuid():N}";
        var oldFile = await factory.CleanAssetAsync(FileWithMarker(marker, 301), "old.png", "image/png", show: false);
        var record = Evt.Id();
        using (var _ = await factory.Cms().PostOkAsync(Evt.Publish(record, 1, 1, new { file = oldFile })))
        {
            await factory.DrainAsync();
        }

        Assert.True(OnDisk(marker));

        // Takedown, then the same id published again with different content, all in one batch.
        using (var _ = await factory.Cms().PostOkAsync(Evt.Delete(record, 10), Evt.Publish(record, 1, 20, new { title = "fresh" })))
        {
            await factory.DrainAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetEntityAsync(record)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Admin().GetAsync($"/api/assets/{oldFile}")).StatusCode);
        Assert.False(OnDisk(marker), "the deleted record's file survived because the same batch re-created the record");
    }

    [Fact]
    public async Task A_re_created_record_keeps_a_file_it_names_again()
    {
        var marker = $"RENAMED-{Guid.NewGuid():N}";
        var file = await factory.CleanAssetAsync(FileWithMarker(marker, 311), "again.png", "image/png", show: false);
        var record = Evt.Id();
        using (var _ = await factory.Cms().PostOkAsync(Evt.Publish(record, 1, 1, new { file })))
        {
            await factory.DrainAsync();
        }

        using (var _ = await factory.Cms().PostOkAsync(Evt.Delete(record, 10), Evt.Publish(record, 1, 20, new { file })))
        {
            await factory.DrainAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetAsync($"/api/assets/{file}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_file_while_identical_bytes_are_being_cleared_never_leaves_the_new_one_empty()
    {
        for (var round = 0; round < 15; round++)
        {
            var bytes = FileWithMarker($"RACE-{round}-{Guid.NewGuid():N}", 400 + round);
            var first = await factory.CleanAssetAsync(bytes, "first.png", "image/png", show: false);
            var (response, body) = await factory.PutAssetAsync(bytes, "second.png", "image/png");
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var second = body.RootElement.GetProperty("id").GetGuid();

            await Task.WhenAll(
                Task.Run(async () => Assert.Equal(HttpStatusCode.NoContent, (await factory.Admin().DeleteAsync($"/api/assets/{first}")).StatusCode)),
                Task.Run(factory.ScanAllAsync));

            Assert.Equal("clean", await factory.StateAsync(second));
            Assert.Equal(bytes, await factory.Admin().GetByteArrayAsync($"/api/assets/{second}"));
        }
    }
}
