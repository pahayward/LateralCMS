using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Assets;

/// <summary>What one sweep removed.</summary>
public sealed record SweepReport(int ExpiredUploads, int OrphanFiles);

/// <summary>
/// Housekeeping: removes multipart uploads that expired, and files that no row accounts for (parts of an upload that is gone,
/// quarantined files of deleted or rejected assets, content-addressed blobs nothing references, half-written temp files).
/// It also retries wipes that earlier failed because a file was in use. It holds the write gate while it compares files with
/// rows, and ignores files younger than <see cref="AssetOptions.SweepGrace"/>, so it can never take a file that is still being registered.
/// </summary>
public sealed class AssetSweepService(
    CmsWriteDbContext db, WriteGate gate, IAssetStore store, TimeProvider time, IOptions<AssetOptions> options, ILogger<AssetSweepService> logger)
{
    public async Task<SweepReport> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var expired = 0;
        var orphans = 0;
        var oldest = DateTime.UtcNow - settings.SweepGrace;

        using var _ = await gate.AcquireAsync(cancellationToken);
        db.ChangeTracker.Clear();
        var now = time.GetUtcNow().UtcDateTime;

        // 1. Uploads that expired: forget their parts.
        var gone = await db.Uploads.Where(u => u.ExpiresAt <= now).Select(u => u.UploadId).ToListAsync(cancellationToken);
        foreach (var uploadId in gone)
        {
            await db.UploadParts.Where(p => p.UploadId == uploadId).ExecuteDeleteAsync(cancellationToken);
            await db.Uploads.Where(u => u.UploadId == uploadId).ExecuteDeleteAsync(cancellationToken);
            store.DeleteUploadParts(uploadId);
            expired++;
        }

        // 2. Part folders that belong to no upload.
        var live = (await db.Uploads.Select(u => u.UploadId).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var uploadId in store.UploadDirectories().Where(id => !live.Contains(id)))
        {
            store.DeleteUploadParts(uploadId);
            orphans++;
        }

        // 3. Quarantined files whose asset is not waiting in quarantine; stale temp files anywhere.
        var waiting = (await db.Assets.Where(a => a.State == AssetState.Quarantined || a.State == AssetState.Failed).Select(a => a.Id).ToListAsync(cancellationToken))
            .Select(id => id.ToString("N")).ToHashSet();
        foreach (var file in store.Files(AssetArea.Quarantine).Where(f => f.LastWriteTimeUtc < oldest).ToList())
        {
            if (FileAssetStore.IsTemp(file) || !waiting.Contains(file.Name))
            {
                if (await store.WipeAsync(file.FullName, cancellationToken))
                {
                    orphans++;
                }
            }
        }

        foreach (var file in store.Files(AssetArea.Blobs).Concat(store.Files(AssetArea.Parts)).Where(f => FileAssetStore.IsTemp(f) && f.LastWriteTimeUtc < oldest).ToList())
        {
            if (await store.WipeAsync(file.FullName, cancellationToken))
            {
                orphans++;
            }
        }

        // 4. Content-addressed blobs that no asset still needs (rejected, deleted, or a wipe that could not run earlier).
        var needed = (await db.Assets.Where(a => a.State != AssetState.Rejected).Select(a => a.Sha256).Distinct().ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in store.Files(AssetArea.Blobs).Where(f => !FileAssetStore.IsTemp(f) && f.LastWriteTimeUtc < oldest && !needed.Contains(f.Name)).ToList())
        {
            if (await store.WipeAsync(file.FullName, cancellationToken))
            {
                orphans++;
            }
        }

        if (expired + orphans > 0)
        {
            logger.LogInformation("Asset sweep removed {Expired} expired uploads and {Orphans} orphan files", expired, orphans);
        }

        return new SweepReport(expired, orphans);
    }
}

/// <summary>Runs <see cref="ScanProcessor"/> in the background: drains the quarantine queue, then waits for a new upload or the poll interval.</summary>
public sealed class ScanWorker(IServiceScopeFactory scopes, ScanSignal signal, IOptions<AssetOptions> options, ILogger<ScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            logger.LogWarning("The asset scan worker is switched off (Assets:WorkerEnabled=false). Uploaded assets stay quarantined until it runs");
            return;
        }

        logger.LogInformation("The asset scan worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var step = await scope.ServiceProvider.GetRequiredService<ScanProcessor>().ProcessNextAsync(stoppingToken);
                if (step == ScanStep.Idle)
                {
                    await signal.WaitAsync(settings.ScanPollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the worker die: an unhandled exception in a hosted service would stop the whole host.
                logger.LogError(ex, "The asset scan worker hit an unexpected error and will try again shortly");
                try
                {
                    await Task.Delay(settings.ScanPollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("The asset scan worker stopped");
    }
}

/// <summary>Runs <see cref="AssetSweepService"/> on a timer.</summary>
public sealed class AssetSweeper(IServiceScopeFactory scopes, IOptions<AssetOptions> options, ILogger<AssetSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(settings.SweepInterval, stoppingToken);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AssetSweepService>().SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The asset sweeper hit an unexpected error and will try again at the next interval");
            }
        }
    }
}
