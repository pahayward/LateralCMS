using System.Net;
using System.Text;
using System.Text.Json;
using Lateral.Cms.Assets;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Lateral.Cms.Tests.Assets;

/// <summary>A scanner timeout long enough that nothing on a busy machine can outlast it.</summary>
public sealed class SlowScanFactory : AssetFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
        base.ExtraSettings.Append(new("Assets:ScanTimeout", "00:00:03"));
}

/// <summary>
/// Quarantine to verdict: nothing is served until the scanner says clean; a flagged asset is wiped and keeps only its verdict;
/// and every way the scan can go wrong leaves the asset quarantined and unserved (it fails closed).
/// </summary>
[Trait("req", "SC-02")]
[Trait("req", "BL-04")]
public class ScanPipelineTests(AssetFactory factory) : IClassFixture<AssetFactory>
{
    private static string Hex(Guid id) => id.ToString().ToUpperInvariant();

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private async Task<Guid> UploadAsync(byte[] bytes, string key = "f.bin", string type = "application/octet-stream")
    {
        var (response, body) = await factory.PutAssetAsync(bytes, key, type);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private async Task AssertNotServedAsync(Guid id)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await factory.User().GetAsync($"/api/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Admin().GetAsync($"/api/assets/{id}")).StatusCode);
    }

    // ---- clean --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_clean_asset_moves_from_quarantine_to_its_content_address_and_is_then_served()
    {
        var bytes = AssetFactory.Png(7_000, 51);
        var id = await UploadAsync(bytes, "ok.png", "image/png");
        await AssertNotServedAsync(id); // quarantined: not served

        var steps = await factory.ScanAllAsync();

        Assert.Contains(ScanStep.Cleared, steps);
        using var status = await factory.StatusAsync(id);
        Assert.Equal("clean", status.RootElement.GetProperty("state").GetString());
        Assert.Equal("clean", status.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("image/png", status.RootElement.GetProperty("detectedType").GetString());
        Assert.Equal("mock-1", status.RootElement.GetProperty("scannerVersion").GetString());
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.EndsWith(id.ToString("N")));
        Assert.Contains(factory.FilesOnDisk(), f => f.EndsWith(AssetFactory.Sha(bytes)) && f.Contains("blobs"));
        await factory.ShowAsync(id); // users see a file once a record they can see names it
        Assert.Equal(bytes, await factory.User().GetByteArrayAsync($"/api/assets/{id}"));
    }

    // ---- flagged ------------------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "SC-04")]
    public async Task A_flagged_asset_is_rejected_its_bytes_are_wiped_from_disk_and_only_the_verdict_remains()
    {
        var bytes = Text("ordinary words " + MockAssetScanner.TestSignatureText + " more words");
        var id = await UploadAsync(bytes, "bad.txt", "text/plain");
        Assert.Contains(factory.FilesOnDisk(), f => f.EndsWith(id.ToString("N")));

        var steps = await factory.ScanAllAsync();

        Assert.Contains(ScanStep.Rejected, steps);
        using var status = await factory.StatusAsync(id);
        Assert.Equal("rejected", status.RootElement.GetProperty("state").GetString());
        Assert.Equal("infected", status.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("Mock.TestSignature", status.RootElement.GetProperty("signature").GetString());
        Assert.Equal(AssetFactory.Sha(bytes), status.RootElement.GetProperty("sha256").GetString());

        // Nothing of the content is left anywhere on disk, under either name.
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.EndsWith(id.ToString("N")) || f.EndsWith(AssetFactory.Sha(bytes)));
        Assert.DoesNotContain(factory.FilesOnDisk(), f => File.ReadAllText(f).Contains(MockAssetScanner.TestSignatureText));
        await AssertNotServedAsync(id);
    }

    [Theory]
    [InlineData("exe-as-image", "image/png", new byte[] { 0x4D, 0x5A, 0x90, 0x00, 3, 3, 3 }, "suspicious", "Mock.ExecutableDisguised")]
    [InlineData("exe", "application/x-msdownload", new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1 }, "infected", "Mock.Executable")]
    public async Task Executables_are_rejected_with_the_rule_that_caught_them(string key, string declared, byte[] bytes, string verdict, string signature)
    {
        var id = await UploadAsync(bytes, key, declared);

        await factory.ScanAllAsync();

        using var status = await factory.StatusAsync(id);
        Assert.Equal("rejected", status.RootElement.GetProperty("state").GetString());
        Assert.Equal(verdict, status.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(signature, status.RootElement.GetProperty("signature").GetString());
        Assert.DoesNotContain(factory.FilesOnDisk(), f => f.EndsWith(id.ToString("N")));
        await AssertNotServedAsync(id);
    }

    [Fact]
    public async Task A_rejected_asset_no_longer_counts_against_the_store_so_its_space_is_free_again()
    {
        var bytes = Text(MockAssetScanner.TestSignatureText + new string('x', 5_000));
        var before = factory.Count("SELECT COALESCE(SUM(Size), 0) FROM Assets WHERE State <> 'rejected'");
        var id = await UploadAsync(bytes, "space.txt", "text/plain");
        Assert.Equal(before + bytes.Length, factory.Count("SELECT COALESCE(SUM(Size), 0) FROM Assets WHERE State <> 'rejected'"));

        await factory.ScanAllAsync();

        Assert.Equal(before, factory.Count("SELECT COALESCE(SUM(Size), 0) FROM Assets WHERE State <> 'rejected'"));
        Assert.Equal("rejected", await factory.StateAsync(id));
    }

    [Fact]
    public async Task When_an_asset_is_rejected_others_that_share_its_content_are_rejected_with_it()
    {
        // The scanner's rules are deterministic, so two assets with the same bytes get the same verdict; this stages the case
        // where an asset was cleared earlier (by older rules) and a new copy of the same content is now flagged.
        var bytes = Text(MockAssetScanner.TestSignatureText + " shared " + Guid.NewGuid());
        var sha = AssetFactory.Sha(bytes);
        var older = Guid.NewGuid();
        factory.Sql(
            "INSERT INTO Assets (Id, Key, ContentType, Size, Sha256, State, CreatedAt, Attempts, NextAttemptAt, Verdict) " +
            $"VALUES ('{Hex(older)}', 'older', 'text/plain', {bytes.Length}, '{sha}', 'clean', '2024-01-01 00:00:00', 0, '2024-01-01 00:00:00', 'clean')");
        var fresh = await UploadAsync(bytes, "fresh.txt", "text/plain");

        await factory.ScanAllAsync();

        Assert.Equal("rejected", await factory.StateAsync(fresh));
        Assert.Equal("rejected", (string)factory.Sql($"SELECT State FROM Assets WHERE Id = '{Hex(older)}'")[0][0]!);
    }

    // ---- fail closed --------------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "SC-03")]
    public async Task A_scanner_error_keeps_the_asset_quarantined_retries_with_back_off_and_sets_it_aside_without_ever_serving_it()
    {
        await factory.SettleScansAsync();
        var bytes = Text("harmless text " + MockAssetScanner.ErrorMarker + Guid.NewGuid());
        var id = await UploadAsync(bytes, "flaky.txt", "text/plain");

        Assert.Equal(ScanStep.Retrying, await factory.ScanStepAsync()); // attempt 1 fails; next in 10 s
        await AssertNotServedAsync(id);
        Assert.Equal(ScanStep.Idle, await factory.ScanStepAsync()); // not due yet

        factory.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ScanStep.Retrying, await factory.ScanStepAsync()); // attempt 2 fails; next in 20 s
        factory.Time.Advance(TimeSpan.FromSeconds(19));
        Assert.Equal(ScanStep.Idle, await factory.ScanStepAsync());
        factory.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ScanStep.SetAside, await factory.ScanStepAsync()); // attempt 3 is the last

        using var status = await factory.StatusAsync(id);
        Assert.Equal("failed", status.RootElement.GetProperty("state").GetString());
        Assert.Equal(3, status.RootElement.GetProperty("attempts").GetInt32());
        Assert.Equal("InvalidOperationException", status.RootElement.GetProperty("lastError").GetString());
        Assert.DoesNotContain("told to fail", status.RootElement.GetRawText());

        // Still not served, and its bytes are still in quarantine for an admin to look at; set aside means not picked up again.
        await AssertNotServedAsync(id);
        Assert.Contains(factory.FilesOnDisk(), f => f.EndsWith(id.ToString("N")));
        factory.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ScanStep.Idle, await factory.ScanStepAsync());
        Assert.Contains(factory.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(id.ToString()) && e.Message.Contains("never served"));
    }

    [Fact]
    [Trait("req", "SC-03")]
    public async Task A_scan_that_hangs_is_cut_off_by_the_timeout_and_counts_as_a_failed_attempt_never_as_clean()
    {
        await factory.SettleScansAsync();
        var id = await UploadAsync(Text("hangs " + MockAssetScanner.TimeoutMarker + Guid.NewGuid()), "hang.txt", "text/plain");

        Assert.Equal(ScanStep.Retrying, await factory.ScanStepAsync());

        using var status = await factory.StatusAsync(id);
        Assert.Equal("quarantined", status.RootElement.GetProperty("state").GetString());
        Assert.Equal("ScanTimeout", status.RootElement.GetProperty("lastError").GetString());
        await AssertNotServedAsync(id);
        await factory.SettleScansAsync();
    }

    [Fact]
    [Trait("req", "SC-03")]
    public async Task If_the_stored_file_has_gone_missing_the_asset_stays_quarantined_and_is_not_declared_clean()
    {
        await factory.SettleScansAsync();
        var id = await UploadAsync(Text("will vanish " + Guid.NewGuid()), "gone.txt", "text/plain");
        File.Delete(factory.FilesOnDisk().Single(f => f.EndsWith(id.ToString("N"))));

        Assert.Equal(ScanStep.Retrying, await factory.ScanStepAsync());

        using var status = await factory.StatusAsync(id);
        Assert.Equal("quarantined", status.RootElement.GetProperty("state").GetString());
        Assert.Equal("ContentMissing", status.RootElement.GetProperty("lastError").GetString());
        await AssertNotServedAsync(id);
        await factory.SettleScansAsync();
    }

    [Fact]
    [Trait("req", "SC-03")]
    public async Task An_admin_can_requeue_a_set_aside_asset_and_only_a_set_aside_one()
    {
        await factory.SettleScansAsync();
        var id = await UploadAsync(Text("retry me " + MockAssetScanner.ErrorMarker + Guid.NewGuid()), "requeue.txt", "text/plain");
        for (var i = 0; i < 3; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(5));
            await factory.ScanStepAsync();
        }

        Assert.Equal("failed", await factory.StateAsync(id));
        var waiting = await UploadAsync(Text("fine " + Guid.NewGuid()), "waiting.txt", "text/plain");

        var notFailed = await factory.Admin().PostAsync($"/api/admin/assets/{waiting}/requeue", null);
        var requeued = await factory.Admin().PostAsync($"/api/admin/assets/{id}/requeue", null);
        var asUser = await factory.User().PostAsync($"/api/admin/assets/{id}/requeue", null);

        Assert.Equal(HttpStatusCode.Conflict, notFailed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, requeued.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asUser.StatusCode);
        using var status = await factory.StatusAsync(id);
        Assert.Equal("quarantined", status.RootElement.GetProperty("state").GetString());
        Assert.False(status.RootElement.TryGetProperty("attempts", out _));

        // It fails again (the marker is still in the content) and is set aside again: requeue does not bypass the scan.
        for (var i = 0; i < 3; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(5));
            await factory.ScanStepAsync();
        }

        Assert.Equal("failed", await factory.StateAsync(id));
        await factory.ScanAllAsync();
    }

    [Fact]
    public async Task A_slow_scan_does_not_hold_up_ingestion_because_it_runs_outside_the_write_gate()
    {
        // Its own app, with a scan timeout of three seconds: the batch must be accepted while the scan is still hanging, and
        // nothing a busy test machine does can make that take longer than the timeout.
        using var slow = new SlowScanFactory();
        var (put, _) = await slow.PutAssetAsync(Text("slow " + MockAssetScanner.TimeoutMarker + Guid.NewGuid()), "slow.txt", "text/plain");
        Assert.Equal(HttpStatusCode.Accepted, put.StatusCode);

        var scan = slow.ScanStepAsync(); // hangs for the whole scan timeout
        using var ack = await slow.AcceptAsync(Evt.Publish(Evt.Id(), 1, 1));

        Assert.False(scan.IsCompleted, "the ingestion batch should have been accepted while the scanner was still busy");
        await scan;
        await slow.DrainAsync();
    }

    [Fact]
    public async Task An_asset_deleted_while_it_is_being_scanned_is_skipped_and_nothing_is_resurrected()
    {
        await factory.SettleScansAsync();
        var id = await UploadAsync(Text("deleted mid-scan " + MockAssetScanner.TimeoutMarker + Guid.NewGuid()), "mid.txt", "text/plain");

        var scan = factory.ScanStepAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await factory.Admin().DeleteAsync($"/api/assets/{id}")).StatusCode);

        Assert.Equal(ScanStep.Skipped, await scan);
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM Assets WHERE Id = '{Hex(id)}'"));
    }

    // ---- re-scan ------------------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "SC-05")]
    public async Task Assets_cleared_by_an_older_scanner_version_can_be_re_queued_and_are_not_served_until_cleared_again()
    {
        await factory.SettleScansAsync();
        var id = await factory.CleanAssetAsync(AssetFactory.Png(900, 61), "old.png", "image/png");
        factory.Sql($"UPDATE Assets SET ScannerVersion = 'mock-0' WHERE Id = '{Hex(id)}'");

        var response = await factory.Admin().PostAsync("/api/admin/assets/rescan-outdated", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await response.ReadJsonAsync())
        {
            Assert.Equal("mock-1", body.RootElement.GetProperty("scannerVersion").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("queued").GetInt32());
        }

        Assert.Equal("quarantined", await factory.StateAsync(id));
        await AssertNotServedAsync(id); // fail closed while it is being re-checked
        await factory.ScanAllAsync();
        Assert.Equal("clean", await factory.StateAsync(id));
        Assert.Equal("mock-1", (string)factory.Sql($"SELECT ScannerVersion FROM Assets WHERE Id = '{Hex(id)}'")[0][0]!);
        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetAsync($"/api/assets/{id}")).StatusCode);
    }

    [Fact]
    [Trait("req", "SC-05")]
    public async Task An_admin_can_re_scan_one_clean_asset_and_not_one_that_is_not_clean()
    {
        var id = await factory.CleanAssetAsync(AssetFactory.Png(600, 62), "again.png", "image/png");
        var waiting = await UploadAsync(Text("w " + Guid.NewGuid()), "w.txt", "text/plain");

        var ok = await factory.Admin().PostAsync($"/api/admin/assets/{id}/rescan", null);
        var refused = await factory.Admin().PostAsync($"/api/admin/assets/{waiting}/rescan", null);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await AssertNotServedAsync(id);
        await factory.ScanAllAsync();
        Assert.Equal("clean", await factory.StateAsync(id));
    }

    // ---- audit --------------------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "SC-06")]
    [Trait("req", "SH-05")]
    public async Task Uploads_verdicts_and_admin_actions_are_written_to_an_audit_log_that_cannot_be_edited()
    {
        var clean = await factory.CleanAssetAsync(AssetFactory.Png(400, 71), "audit.png", "image/png");
        var bad = await UploadAsync(Text(MockAssetScanner.TestSignatureText + Guid.NewGuid()), "audit-bad.txt", "text/plain");
        await factory.ScanAllAsync();
        await factory.Admin().DeleteAsync($"/api/assets/{clean}");

        string[] ActionsFor(Guid id) =>
            [.. factory.Sql($"SELECT Action FROM AuditLog WHERE Target = '{id}' COLLATE NOCASE ORDER BY Id").Select(r => (string)r[0]!)];
        var actors = factory.Sql($"SELECT Actor FROM AuditLog WHERE Target = '{clean}' COLLATE NOCASE ORDER BY Id").Select(r => (string)r[0]!).ToArray();

        Assert.Equal(["asset.uploaded", "asset.cleared", "asset.deleted"], ActionsFor(clean));
        Assert.Equal(["asset.uploaded", "asset.rejected"], ActionsFor(bad));
        Assert.Equal([CmsFactory.CmsUser, "scanner", CmsFactory.AdminName], actors);

        // No content, no secrets: only ids, sizes, verdict words and rule names.
        var everything = string.Join('\n', factory.Sql("SELECT Detail FROM AuditLog WHERE Detail IS NOT NULL").Select(r => (string)r[0]!));
        Assert.DoesNotContain(CmsFactory.CmsPassword, everything);

        var update = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => factory.Sql("UPDATE AuditLog SET Actor = 'someone else'"));
        var delete = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => factory.Sql("DELETE FROM AuditLog"));
        Assert.Contains("append-only", update.Message);
        Assert.Contains("append-only", delete.Message);
    }

    // ---- the real worker ----------------------------------------------------------------------------------

    public sealed class LiveScanFactory : AssetFactory
    {
        protected override bool AssetWorkerEnabled => true;
    }

    [Fact]
    public async Task The_background_worker_scans_a_new_upload_without_being_asked()
    {
        using var live = new LiveScanFactory();
        var bytes = AssetFactory.Png(1_500, 81);

        var (response, body) = await live.PutAssetAsync(bytes, "live.png", "image/png");
        var id = body.RootElement.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await CmsFactory.WaitUntilAsync(async () => await live.StateAsync(id) == "clean", "the worker to clear the asset");
        await live.ShowAsync(id);
        Assert.Equal(bytes, await live.User().GetByteArrayAsync($"/api/assets/{id}"));
    }
}
