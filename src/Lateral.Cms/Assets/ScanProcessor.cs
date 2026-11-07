using Lateral.Cms.Ingestion;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Assets;

/// <summary>Wakes the scan worker when an asset is quarantined.</summary>
public sealed class ScanSignal : WakeSignal;

/// <summary>What one call to <see cref="ScanProcessor.ProcessNextAsync"/> did.</summary>
public enum ScanStep
{
    Idle,

    /// <summary>The scanner cleared the asset; it can be downloaded.</summary>
    Cleared,

    /// <summary>The scanner flagged it; the bytes were wiped.</summary>
    Rejected,

    /// <summary>The scan failed or timed out; the asset stays quarantined and will be tried again.</summary>
    Retrying,

    /// <summary>The scan failed for the last time; the asset stays quarantined and out of the queue until an admin requeues it.</summary>
    SetAside,

    /// <summary>The asset changed or vanished while it was being scanned; the verdict was not applied.</summary>
    Skipped,
}

/// <summary>
/// Takes the next quarantined asset and decides its fate. **Fail closed:** the only way out of quarantine into service is a
/// <see cref="ScanVerdict.Clean"/> verdict. A scanner error, a timeout, a missing file or an exception leaves the asset
/// quarantined, counts as a failed attempt, and is retried with a growing delay; after the maximum it is set aside, still
/// unserved, for an admin to requeue. The scan itself runs outside the write gate, so a slow scan never holds up ingestion.
/// </summary>
public sealed class ScanProcessor(
    CmsWriteDbContext db,
    WriteGate gate,
    IAssetStore store,
    IAssetScanner scanner,
    TimeProvider time,
    IOptions<AssetOptions> options,
    ILogger<ScanProcessor> logger)
{
    /// <summary>The assets that may be scanned now, oldest first. Public so a test can check the SQL uses <c>IX_Assets_Scan</c>.</summary>
    public static IQueryable<AssetRow> NextDue(IQueryable<AssetRow> assets, DateTime now) =>
        assets.Where(a => a.State == AssetState.Quarantined && a.NextAttemptAt <= now).OrderBy(a => a.CreatedAt);

    public async Task<ScanStep> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        AssetRow? pick;
        using (await gate.AcquireAsync(cancellationToken))
        {
            db.ChangeTracker.Clear();
            pick = await NextDue(db.Assets.AsNoTracking(), time.GetUtcNow().UtcDateTime).FirstOrDefaultAsync(cancellationToken);
        }

        if (pick is null)
        {
            return ScanStep.Idle;
        }

        // A re-scan reads the cleared copy; a new upload reads the quarantined one.
        var quarantine = store.QuarantinePath(pick.Id);
        var path = store.Exists(quarantine) ? quarantine : store.BlobPath(pick.Sha256);

        ScanResult? result = null;
        string? failure = null;
        try
        {
            if (!store.Exists(path))
            {
                failure = "ContentMissing";
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.ScanTimeout);
                try
                {
                    await using var stream = store.OpenRead(path);
                    result = await scanner.ScanAsync(stream, new AssetInfo(pick.Key, pick.ContentType, pick.Size), timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    failure = "ScanTimeout";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex.GetType().Name;
            logger.LogWarning(ex, "The scanner failed on asset {AssetId}", pick.Id);
        }

        if (failure is null && result is { Verdict: ScanVerdict.Error })
        {
            failure = "ScannerError";
        }

        using (await gate.AcquireAsync(cancellationToken))
        {
            db.ChangeTracker.Clear();
            var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == pick.Id, cancellationToken);
            if (asset is null || asset.State != AssetState.Quarantined)
            {
                return ScanStep.Skipped;
            }

            var now = time.GetUtcNow().UtcDateTime;
            if (failure is not null || result is null)
            {
                return await RecordFailureAsync(asset, failure ?? "NoResult", now, settings, cancellationToken);
            }

            asset.DetectedType = result.DetectedType;
            asset.ScannedAt = now;
            asset.ScannerName = result.ScannerName;
            asset.ScannerVersion = result.ScannerVersion;
            asset.Attempts = 0;
            asset.LastError = null;

            if (result.Verdict == ScanVerdict.Clean)
            {
                if (store.Exists(quarantine))
                {
                    await store.PromoteAsync(quarantine, asset.Sha256, cancellationToken);
                }

                asset.State = AssetState.Clean;
                asset.Verdict = "clean";
                asset.Signature = null;
                db.Audit(now, "scanner", "asset.cleared", asset.Id.ToString(), $"{result.ScannerName} {result.ScannerVersion}");
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Asset {AssetId} cleared by {Scanner} {Version}", asset.Id, result.ScannerName, result.ScannerVersion);
                return ScanStep.Cleared;
            }

            // Flagged. Record the verdict first, so nothing is marked servable while its bytes are being removed, then wipe.
            asset.State = AssetState.Rejected;
            asset.Verdict = result.Verdict == ScanVerdict.Infected ? "infected" : "suspicious";
            asset.Signature = result.Signature;
            db.Audit(now, "scanner", "asset.rejected", asset.Id.ToString(), $"{asset.Verdict}: {result.Signature}");

            // Other assets with the same content share the blob that is about to go; they are rejected too.
            var sharing = await db.Assets.Where(a => a.Sha256 == asset.Sha256 && a.Id != asset.Id && a.State == AssetState.Clean).ToListAsync(cancellationToken);
            foreach (var other in sharing)
            {
                other.State = AssetState.Rejected;
                other.Verdict = asset.Verdict;
                other.Signature = result.Signature;
                other.ScannedAt = now;
                db.Audit(now, "scanner", "asset.rejected", other.Id.ToString(), $"same content as {asset.Id}");
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Asset {AssetId} rejected by {Scanner}: {Verdict} {Signature}", asset.Id, result.ScannerName, asset.Verdict, result.Signature);

            await store.WipeAsync(quarantine, cancellationToken);
            if (!await store.WipeAsync(store.BlobPath(asset.Sha256), cancellationToken))
            {
                logger.LogWarning("The content of rejected asset {AssetId} could not be removed yet; the sweeper will retry", asset.Id);
            }

            return ScanStep.Rejected;
        }
    }

    private async Task<ScanStep> RecordFailureAsync(AssetRow asset, string kind, DateTime now, AssetOptions settings, CancellationToken cancellationToken)
    {
        asset.Attempts++;
        asset.LastError = kind;
        var final = asset.Attempts >= settings.ScanMaxAttempts;
        asset.State = final ? AssetState.Failed : AssetState.Quarantined;
        asset.NextAttemptAt = final ? now : now + settings.ScanDelayBefore(asset.Attempts + 1);
        if (final)
        {
            db.Audit(now, "scanner", "asset.scan_set_aside", asset.Id.ToString(), kind);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (final)
        {
            logger.LogError("Asset {AssetId} could not be scanned after {Attempts} attempts ({Kind}) and was set aside. It stays quarantined and is never served", asset.Id, asset.Attempts, kind);
            return ScanStep.SetAside;
        }

        logger.LogWarning("Scan attempt {Attempt} of asset {AssetId} failed ({Kind}); it stays quarantined and will be retried after {Next:O}", asset.Attempts, asset.Id, kind, asset.NextAttemptAt);
        return ScanStep.Retrying;
    }
}
