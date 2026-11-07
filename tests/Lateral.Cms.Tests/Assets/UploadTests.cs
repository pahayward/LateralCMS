using System.Net;
using System.Text.Json;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Assets;

/// <summary>Getting bytes in: one PUT for small objects, S3-style multipart for large ones, every refusal storing nothing.</summary>
[Trait("req", "BL-01")]
public class UploadTests(AssetFactory factory) : IClassFixture<AssetFactory>
{
    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = await response.ReadJsonAsync();
        return body.RootElement.GetProperty("code").GetString()!;
    }

    // ---- the single PUT ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_small_object_is_stored_in_quarantine_with_its_checksum_and_is_not_served()
    {
        var bytes = AssetFactory.Png(10_000);

        var (response, body) = await factory.PutAssetAsync(bytes, "photo one.png", "image/png");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal($"/cms/assets/{id}", response.Headers.Location!.ToString());
        Assert.Equal("quarantined", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(AssetFactory.Sha(bytes), body.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(10_000, body.RootElement.GetProperty("size").GetInt64());
        Assert.Equal("photo one.png", body.RootElement.GetProperty("key").GetString());
        Assert.Equal(1, factory.Count($"SELECT COUNT(*) FROM Assets WHERE Id = '{id.ToString().ToUpperInvariant()}'"));

        // Stored on disk, in quarantine, not under its content address yet, and not downloadable.
        Assert.Contains(factory.FilesOnDisk(), f => f.EndsWith(id.ToString("N")) && f.Contains("quarantine"));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.User().GetAsync($"/api/assets/{id}")).StatusCode);
        await factory.ScanAllAsync();
    }

    [Fact]
    public async Task A_matching_sha256_header_is_accepted_and_a_wrong_one_stores_nothing()
    {
        var bytes = AssetFactory.Png(2_000, 5);
        var before = factory.Count("SELECT COUNT(*) FROM Assets");
        var filesBefore = factory.FilesOnDisk().Length;

        var good = new HttpRequestMessage(HttpMethod.Put, "/cms/assets/with-hash") { Content = AssetFactory.Body(bytes) };
        good.Headers.Add("X-Content-SHA256", AssetFactory.Sha(bytes));
        var bad = new HttpRequestMessage(HttpMethod.Put, "/cms/assets/wrong-hash") { Content = AssetFactory.Body(bytes) };
        bad.Headers.Add("X-Content-SHA256", new string('0', 64));

        var ok = await factory.Cms().SendAsync(good);
        var refused = await factory.Cms().SendAsync(bad);

        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("checksum_mismatch", await CodeAsync(refused));
        Assert.Equal(before + 1, factory.Count("SELECT COUNT(*) FROM Assets"));
        Assert.Equal(filesBefore + 1, factory.FilesOnDisk().Length);
        await factory.ScanAllAsync();
    }

    [Fact]
    public async Task A_single_put_over_the_simple_limit_is_refused_by_its_declared_length_before_it_is_read()
    {
        var before = factory.FilesOnDisk().Length;

        var response = await factory.Cms().PutAsync("/cms/assets/too-big", AssetFactory.Body(new byte[AssetFactory.MaxSimple + 1]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", await CodeAsync(response));
        Assert.Equal(before, factory.FilesOnDisk().Length);
    }

    [Fact]
    public async Task A_chunked_body_over_the_limit_is_cut_off_while_it_is_read_and_nothing_stays_on_disk()
    {
        var before = factory.FilesOnDisk().Length;
        var content = new StreamContent(new MemoryStream(new byte[AssetFactory.MaxSimple * 3]));
        content.Headers.ContentLength = null; // no declared length: the server must enforce the limit as it reads

        var request = new HttpRequestMessage(HttpMethod.Put, "/cms/assets/chunked") { Content = content };
        request.Headers.TransferEncodingChunked = true;
        var response = await factory.Cms().SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(before, factory.FilesOnDisk().Length);
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.Contains(".tmp-"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a%2Fb")]
    [InlineData("a%5Cb")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("bad%00key")]
    public async Task A_key_can_never_steer_a_path_because_files_are_named_by_id_and_content_only(string key)
    {
        // The server may refuse the key or store it as a plain label; either way no file name comes from it.
        var failure = await Record.ExceptionAsync(() => factory.Cms().PutAsync($"/cms/assets/{key}", AssetFactory.Body([1, 2, 3])));

        Assert.True(failure is null or InvalidOperationException, failure?.ToString());
        var names = new System.Text.RegularExpressions.Regex(@"^([0-9a-f]{32}|[0-9a-f]{64}|\d+)(\.tmp-[0-9a-f]+)?$");
        Assert.All(factory.FilesOnDisk(), f => Assert.Matches(names, Path.GetFileName(f)));
        Assert.All(factory.FilesOnDisk(), f => Assert.StartsWith(Path.GetFullPath(factory.AssetsPath), Path.GetFullPath(f)));
        await factory.ScanAllAsync();
    }

    [Fact]
    public async Task An_empty_asset_is_refused()
    {
        var response = await factory.Cms().PutAsync("/cms/assets/empty", AssetFactory.Body([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("empty_content", await CodeAsync(response));
    }

    [Fact]
    public async Task The_claimed_content_type_is_kept_as_a_claim_and_a_nonsense_one_becomes_octet_stream()
    {
        var (_, one) = await factory.PutAssetAsync([1, 2, 3, 4], "t1", "image/png");
        var request = new HttpRequestMessage(HttpMethod.Put, "/cms/assets/t2") { Content = new ByteArrayContent([1, 2, 3]) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "this is /not/ a type");
        var response = await factory.Cms().SendAsync(request);
        using var two = await response.ReadJsonAsync();

        Assert.Equal("image/png", one.RootElement.GetProperty("contentType").GetString());
        Assert.Equal("application/octet-stream", two.RootElement.GetProperty("contentType").GetString());
        await factory.ScanAllAsync();
    }

    // ---- multipart --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_multipart_upload_with_parts_sent_in_parallel_assembles_to_exactly_the_original_bytes()
    {
        var bytes = AssetFactory.Png(AssetFactory.MinPart * 9 + 321, 11);

        var (response, uploadId) = await factory.MultipartAsync(bytes, AssetFactory.MinPart * 2);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = await response.ReadJsonAsync();
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(AssetFactory.Sha(bytes), body.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, body.RootElement.GetProperty("size").GetInt64());

        await factory.ScanAllAsync();
        var served = await factory.User().GetByteArrayAsync($"/api/assets/{id}");
        Assert.Equal(bytes, served);

        // The parts are gone once the object is assembled.
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM UploadParts WHERE UploadId = '{uploadId.ToString().ToUpperInvariant()}'"));
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM Uploads WHERE UploadId = '{uploadId.ToString().ToUpperInvariant()}'"));
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.Contains(uploadId.ToString("N")));
    }

    [Fact]
    public async Task Sending_a_part_again_replaces_it_and_the_last_one_wins()
    {
        var first = AssetFactory.Png(AssetFactory.MinPart * 2, 21);
        var second = AssetFactory.Png(AssetFactory.MinPart * 2, 22);
        var initiate = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "replace.bin", size = second.Length }));
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();

        var a = await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(first));
        var again = await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(first));
        var b = await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(second));
        var done = await factory.Cms().PostAsync($"/cms/assets/uploads/{uploadId}/complete", Evt.Json(new { parts = new[] { new { partNumber = 1, sha256 = AssetFactory.Sha(second) } } }));

        Assert.All(new[] { a, again, b }, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(HttpStatusCode.Accepted, done.StatusCode);
        using var body = await done.ReadJsonAsync();
        Assert.Equal(AssetFactory.Sha(second), body.RootElement.GetProperty("sha256").GetString());
        await factory.ScanAllAsync();
    }

    [Fact]
    public async Task A_part_with_a_wrong_checksum_header_is_refused_and_forgotten()
    {
        var initiate = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "x", size = AssetFactory.MinPart * 2 }));
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/cms/assets/uploads/{uploadId}/parts/1") { Content = AssetFactory.Body(new byte[AssetFactory.MinPart]) };
        request.Headers.Add("X-Content-SHA256", new string('f', 64));

        var response = await factory.Cms().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("checksum_mismatch", await CodeAsync(response));
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM UploadParts WHERE UploadId = '{uploadId.ToString().ToUpperInvariant()}'"));
        await factory.Cms().DeleteAsync($"/cms/assets/uploads/{uploadId}");
    }

    [Theory]
    [InlineData("missing_part")]
    [InlineData("wrong_checksum")]
    [InlineData("out_of_order")]
    [InlineData("duplicate")]
    [InlineData("too_small")]
    [InlineData("size_mismatch")]
    [InlineData("empty_list")]
    public async Task Completing_with_a_bad_part_list_is_refused_and_assembles_nothing(string problem)
    {
        var size = AssetFactory.MinPart * 3;
        var initiate = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "bad-list.bin", size }));
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();
        var one = new byte[AssetFactory.MinPart];
        var two = new byte[AssetFactory.MinPart * 2];
        var tiny = new byte[10];
        Assert.Equal(HttpStatusCode.OK, (await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(one))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/2", AssetFactory.Body(two))).StatusCode);
        var assetsBefore = factory.Count("SELECT COUNT(*) FROM Assets");

        if (problem == "too_small")
        {
            await factory.Cms().DeleteAsync($"/cms/assets/uploads/{uploadId}");
            var small = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "small.bin", size = tiny.Length + one.Length }));
            using var smallStarted = await small.ReadJsonAsync();
            uploadId = smallStarted.RootElement.GetProperty("uploadId").GetGuid();
            await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(tiny));
            await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/2", AssetFactory.Body(one));
        }

        object parts = problem switch
        {
            "missing_part" => new[] { new { partNumber = 1, sha256 = (string?)null }, new { partNumber = 9, sha256 = (string?)null } },
            "wrong_checksum" => new[] { new { partNumber = 1, sha256 = (string?)new string('a', 64) }, new { partNumber = 2, sha256 = (string?)null } },
            "out_of_order" => new[] { new { partNumber = 2, sha256 = (string?)null }, new { partNumber = 1, sha256 = (string?)null } },
            "duplicate" => new[] { new { partNumber = 1, sha256 = (string?)null }, new { partNumber = 1, sha256 = (string?)null } },
            "size_mismatch" => new[] { new { partNumber = 2, sha256 = (string?)null } },
            "too_small" => new[] { new { partNumber = 1, sha256 = (string?)null }, new { partNumber = 2, sha256 = (string?)null } },
            _ => Array.Empty<object>(),
        };
        var response = await factory.Cms().PostAsync($"/cms/assets/uploads/{uploadId}/complete", Evt.Json(new { parts }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(assetsBefore, factory.Count("SELECT COUNT(*) FROM Assets"));
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.Contains("quarantine") && f.Contains(".tmp-"));
        await factory.Cms().DeleteAsync($"/cms/assets/uploads/{uploadId}");
    }

    [Fact]
    public async Task Parts_that_add_up_to_more_than_the_declared_size_are_refused()
    {
        var initiate = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "over.bin", size = AssetFactory.MinPart }));
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();

        var response = await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(new byte[AssetFactory.MinPart + 1]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("exceeds_declared_size", await CodeAsync(response));
        await factory.Cms().DeleteAsync($"/cms/assets/uploads/{uploadId}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_part_number_or_size_outside_the_limits_is_refused(int number)
    {
        var initiate = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "n.bin", size = 100 }));
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();

        var badNumber = await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/{number}", AssetFactory.Body([1]));
        var badSize = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "n.bin", size = number }));
        var tooBig = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "n.bin", size = 9L * 1024 * 1024 }));

        Assert.Equal(HttpStatusCode.BadRequest, badNumber.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badSize.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        await factory.Cms().DeleteAsync($"/cms/assets/uploads/{uploadId}");
    }

    [Fact]
    public async Task A_part_over_the_maximum_is_refused_by_its_declared_length()
    {
        var initiate = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "p.bin", size = 4 * 1024 * 1024 }));
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();

        var response = await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(new byte[AssetFactory.MaxPart + 1]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await factory.Cms().DeleteAsync($"/cms/assets/uploads/{uploadId}");
    }

    [Fact]
    public async Task Completing_twice_is_refused_the_second_time_and_aborting_removes_the_parts()
    {
        var bytes = AssetFactory.Png(AssetFactory.MinPart + 5, 31);
        var initiate = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "twice.bin", size = bytes.Length }));
        using var started = await initiate.ReadJsonAsync();
        var uploadId = started.RootElement.GetProperty("uploadId").GetGuid();
        await factory.Cms().PutAsync($"/cms/assets/uploads/{uploadId}/parts/1", AssetFactory.Body(bytes));
        var list = Evt.Json(new { parts = new[] { new { partNumber = 1, sha256 = AssetFactory.Sha(bytes) } } });

        var first = await factory.Cms().PostAsync($"/cms/assets/uploads/{uploadId}/complete", list);
        var second = await factory.Cms().PostAsync($"/cms/assets/uploads/{uploadId}/complete", Evt.Json(new { parts = new[] { new { partNumber = 1, sha256 = AssetFactory.Sha(bytes) } } }));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        var other = await factory.Cms().PostAsync("/cms/assets/uploads", Evt.Json(new { key = "abort.bin", size = 5 }));
        using var otherStarted = await other.ReadJsonAsync();
        var abortId = otherStarted.RootElement.GetProperty("uploadId").GetGuid();
        await factory.Cms().PutAsync($"/cms/assets/uploads/{abortId}/parts/1", AssetFactory.Body([1, 2, 3, 4, 5]));
        Assert.Equal(HttpStatusCode.NoContent, (await factory.Cms().DeleteAsync($"/cms/assets/uploads/{abortId}")).StatusCode);
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM UploadParts WHERE UploadId = '{abortId.ToString().ToUpperInvariant()}'"));
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.Contains(abortId.ToString("N")));
        await factory.ScanAllAsync();
    }

    [Fact]
    public async Task Parts_for_an_upload_that_does_not_exist_are_refused_and_leave_no_file()
    {
        var before = factory.FilesOnDisk().Length;

        var response = await factory.Cms().PutAsync($"/cms/assets/uploads/{Guid.NewGuid()}/parts/1", AssetFactory.Body([1, 2, 3]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("upload_not_found", await CodeAsync(response));
        Assert.Equal(before, factory.FilesOnDisk().Length);
    }

    [Fact]
    public async Task Many_uploads_at_once_each_get_their_own_asset_with_the_right_content()
    {
        var items = Enumerable.Range(0, 12).Select(i => (Key: $"parallel-{i}.bin", Bytes: AssetFactory.Png(3_000 + i, 100 + i))).ToList();

        var results = await Task.WhenAll(items.Select(async item =>
        {
            var (response, body) = await factory.PutAssetAsync(item.Bytes, item.Key);
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode} {body.RootElement}{factory.Errors()}");
            return (item, Id: body.RootElement.GetProperty("id").GetGuid());
        }));
        await factory.ScanAllAsync();

        Assert.Equal(items.Count, results.Select(r => r.Id).Distinct().Count());
        foreach (var (item, id) in results)
        {
            Assert.Equal(item.Bytes, await factory.User().GetByteArrayAsync($"/api/assets/{id}"));
        }
    }

    [Fact]
    public async Task The_status_endpoint_reports_state_and_never_the_bytes()
    {
        var (_, body) = await factory.PutAssetAsync(AssetFactory.Png(500, 41), "status.png", "image/png");
        var id = body.RootElement.GetProperty("id").GetGuid();

        var asCms = await factory.Cms().GetStringAsync($"/cms/assets/{id}");
        var asAdmin = await factory.Admin().GetAsync($"/cms/assets/{id}");

        using var parsed = JsonDocument.Parse(asCms);
        Assert.Equal("quarantined", parsed.RootElement.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.User().GetAsync($"/cms/assets/{id}")).StatusCode);
        await factory.ScanAllAsync();
    }
}
