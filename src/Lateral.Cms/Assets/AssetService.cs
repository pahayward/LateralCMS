using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Assets;

/// <summary>A request the service refuses, with the status and stable code the caller gets. Nothing was kept.</summary>
public sealed class AssetRefusal(int status, string code, string detail) : Exception(detail)
{
    public int Status { get; } = status;

    public string Code { get; } = code;
}

/// <summary>An asset as reported to the CMS and admins. Never the bytes.</summary>
public sealed record AssetView(
    Guid Id,
    string Key,
    string ContentType,
    string? DetectedType,
    long Size,
    string Sha256,
    AssetState State,
    DateTime CreatedAt,
    DateTime? ScannedAt,
    string? Verdict,
    string? Signature,
    string? ScannerVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Attempts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastError)
{
    public static AssetView From(AssetRow a) =>
        new(a.Id, a.Key, a.ContentType, a.DetectedType, a.Size, a.Sha256, a.State, a.CreatedAt, a.ScannedAt, a.Verdict, a.Signature,
            a.ScannerVersion, a.Attempts > 0 ? a.Attempts : null, a.LastError);
}

public sealed record InitiateRequest(string? Key, string? ContentType, long? Size);

public sealed record InitiatedUpload(Guid UploadId, long MinPartBytes, long MaxPartBytes, int MaxParts, DateTime ExpiresAt);

public sealed record PartReceipt(int PartNumber, long Size, string Sha256);

public sealed record CompletedPart(int PartNumber, string? Sha256);

public sealed record CompleteRequest(List<CompletedPart>? Parts, string? Sha256);

/// <summary>What a download needs, for a clean asset only.</summary>
public sealed record ServableAsset(string Path, string ContentType, string FileName, string Sha256, long Size, DateTime CreatedAt);

/// <summary>
/// Everything that creates, finds or changes an asset. It stores bytes through <see cref="IAssetStore"/> and never serves any:
/// an asset becomes servable only when the scan processor marks it clean.
/// </summary>
public sealed class AssetService(
    CmsWriteDbContext db,
    CmsReadDbContext reader,
    WriteGate gate,
    IAssetStore store,
    ScanSignal signal,
    TimeProvider time,
    IOptions<AssetOptions> options,
    ILogger<AssetService> logger)
{
    private AssetOptions Settings => options.Value;

    // ---- uploads ----------------------------------------------------------------------------------------

    /// <summary>One request, one object, up to <see cref="AssetOptions.MaxSimpleBytes"/>. Streamed to disk, never held in memory.</summary>
    public async Task<AssetView> PutAsync(
        string key, string? contentType, long? declaredLength, string? sha256, Stream body, string actor, CancellationToken cancellationToken)
    {
        var name = ValidKey(key);
        if (declaredLength > Settings.MaxSimpleBytes)
        {
            throw new AssetRefusal(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"A single upload may hold at most {Settings.MaxSimpleBytes} bytes. Use a multipart upload for more.");
        }

        await EnsureRoomAsync(declaredLength ?? 0, cancellationToken);

        var id = Guid.NewGuid();
        var path = store.QuarantinePath(id);
        StoredFile stored;
        try
        {
            stored = await store.SaveAsync(body, path, Settings.MaxSimpleBytes, cancellationToken);
        }
        catch (AssetTooLargeException)
        {
            throw new AssetRefusal(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"A single upload may hold at most {Settings.MaxSimpleBytes} bytes. Use a multipart upload for more.");
        }

        try
        {
            RequireChecksum(sha256, stored.Sha256);
            if (stored.Size == 0)
            {
                throw new AssetRefusal(StatusCodes.Status400BadRequest, "empty_content", "An asset cannot be empty.");
            }

            var now = time.GetUtcNow().UtcDateTime;
            AssetRow row;
            using (await gate.AcquireAsync(cancellationToken))
            {
                db.ChangeTracker.Clear();
                if (await UsedBytesAsync(cancellationToken) + stored.Size > Settings.MaxBytes)
                {
                    throw Full();
                }

                row = NewAsset(id, name, ContentTypeOrDefault(contentType), stored, now);
                db.Assets.Add(row);
                db.Audit(now, actor, "asset.uploaded", id.ToString(), $"{stored.Size} bytes, quarantined");
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            signal.Notify();
            return AssetView.From(row);
        }
        catch
        {
            await store.WipeAsync(path, CancellationToken.None);
            throw;
        }
    }

    public async Task<InitiatedUpload> InitiateAsync(InitiateRequest? request, string actor, CancellationToken cancellationToken)
    {
        var name = ValidKey(request?.Key);
        var size = request?.Size ?? 0;
        if (size < 1)
        {
            throw new AssetRefusal(StatusCodes.Status400BadRequest, "invalid_size", "Declare the total size in bytes (at least 1).");
        }

        if (size > Settings.MaxObjectBytes)
        {
            throw new AssetRefusal(StatusCodes.Status413PayloadTooLarge, "payload_too_large", $"An object may hold at most {Settings.MaxObjectBytes} bytes.");
        }

        await EnsureRoomAsync(size, cancellationToken);

        var now = time.GetUtcNow().UtcDateTime;
        var upload = new UploadRow
        {
            UploadId = Guid.NewGuid(),
            Key = name,
            ContentType = ContentTypeOrDefault(request?.ContentType),
            DeclaredSize = size,
            CreatedAt = now,
            ExpiresAt = now + Settings.UploadTtl,
        };

        using (await gate.AcquireAsync(cancellationToken))
        {
            db.ChangeTracker.Clear();
            db.Uploads.Add(upload);
            db.Audit(now, actor, "asset.upload_started", upload.UploadId.ToString(), $"declared {size} bytes");
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        return new InitiatedUpload(upload.UploadId, Settings.MinPartBytes, Settings.MaxPartBytes, Settings.MaxParts, upload.ExpiresAt);
    }

    public async Task<PartReceipt> PutPartAsync(
        Guid uploadId, int partNumber, long? declaredLength, string? sha256, Stream body, CancellationToken cancellationToken)
    {
        if (partNumber < 1 || partNumber > Settings.MaxParts)
        {
            throw new AssetRefusal(StatusCodes.Status400BadRequest, "invalid_part_number", $"Part numbers run from 1 to {Settings.MaxParts}.");
        }

        if (declaredLength > Settings.MaxPartBytes)
        {
            throw new AssetRefusal(StatusCodes.Status413PayloadTooLarge, "payload_too_large", $"A part may hold at most {Settings.MaxPartBytes} bytes.");
        }

        var upload = await FindUploadAsync(uploadId, cancellationToken);
        await EnsureRoomAsync(declaredLength ?? 0, cancellationToken);

        var path = store.PartPath(uploadId, partNumber);
        StoredFile stored;
        try
        {
            stored = await store.SaveAsync(body, path, Settings.MaxPartBytes, cancellationToken);
        }
        catch (AssetTooLargeException)
        {
            throw new AssetRefusal(StatusCodes.Status413PayloadTooLarge, "payload_too_large", $"A part may hold at most {Settings.MaxPartBytes} bytes.");
        }

        try
        {
            RequireChecksum(sha256, stored.Sha256);
            using (await gate.AcquireAsync(cancellationToken))
            {
                db.ChangeTracker.Clear();
                var existing = await db.UploadParts.FindAsync([uploadId, partNumber], cancellationToken);
                var others = await db.UploadParts.Where(p => p.UploadId == uploadId && p.PartNumber != partNumber).SumAsync(p => p.Size, cancellationToken);
                if (others + stored.Size > upload.DeclaredSize)
                {
                    throw new AssetRefusal(StatusCodes.Status400BadRequest, "exceeds_declared_size",
                        "The parts add up to more than the size declared when the upload was started.");
                }

                if (await UsedBytesAsync(cancellationToken) - (existing?.Size ?? 0) + stored.Size > Settings.MaxBytes)
                {
                    throw Full();
                }

                if (existing is null)
                {
                    db.UploadParts.Add(new UploadPartRow { UploadId = uploadId, PartNumber = partNumber, Size = stored.Size, Sha256 = stored.Sha256 });
                }
                else
                {
                    existing.Size = stored.Size;
                    existing.Sha256 = stored.Sha256;
                }

                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            return new PartReceipt(partNumber, stored.Size, stored.Sha256);
        }
        catch
        {
            // The file replaced whatever this part number held, and the row no longer describes it: forget the part.
            await store.WipeAsync(path, CancellationToken.None);
            using (await gate.AcquireAsync(CancellationToken.None))
            {
                db.ChangeTracker.Clear();
                await db.UploadParts.Where(p => p.UploadId == uploadId && p.PartNumber == partNumber).ExecuteDeleteAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<AssetView> CompleteAsync(Guid uploadId, CompleteRequest? request, string actor, CancellationToken cancellationToken)
    {
        var upload = await FindUploadAsync(uploadId, cancellationToken);
        var listed = request?.Parts;
        if (listed is not { Count: > 0 })
        {
            throw new AssetRefusal(StatusCodes.Status400BadRequest, "invalid_part_list", "List the parts to assemble, in ascending order.");
        }

        var stored = await reader.UploadParts.Where(p => p.UploadId == uploadId).ToDictionaryAsync(p => p.PartNumber, cancellationToken);
        var paths = new List<string>();
        long total = 0;
        for (var i = 0; i < listed.Count; i++)
        {
            var part = listed[i];
            if (i > 0 && part.PartNumber <= listed[i - 1].PartNumber)
            {
                throw new AssetRefusal(StatusCodes.Status400BadRequest, "invalid_part_list", "Parts must be listed in ascending order, each once.");
            }

            if (!stored.TryGetValue(part.PartNumber, out var row)
                || (part.Sha256 is not null && !string.Equals(part.Sha256, row.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AssetRefusal(StatusCodes.Status400BadRequest, "invalid_part", $"Part {part.PartNumber} is missing or its checksum differs from what was uploaded.");
            }

            if (i < listed.Count - 1 && row.Size < Settings.MinPartBytes)
            {
                throw new AssetRefusal(StatusCodes.Status400BadRequest, "part_too_small", $"Every part except the last must be at least {Settings.MinPartBytes} bytes.");
            }

            total += row.Size;
            paths.Add(store.PartPath(uploadId, part.PartNumber));
        }

        if (total != upload.DeclaredSize)
        {
            throw new AssetRefusal(StatusCodes.Status400BadRequest, "size_mismatch", $"The parts add up to {total} bytes but {upload.DeclaredSize} were declared.");
        }

        var id = Guid.NewGuid();
        var path = store.QuarantinePath(id);
        var assembled = await store.AssembleAsync(paths, path, cancellationToken);
        try
        {
            RequireChecksum(request?.Sha256, assembled.Sha256);
            var now = time.GetUtcNow().UtcDateTime;
            AssetRow asset;
            using (await gate.AcquireAsync(cancellationToken))
            {
                db.ChangeTracker.Clear();
                if (!await db.Uploads.AnyAsync(u => u.UploadId == uploadId, cancellationToken))
                {
                    throw new AssetRefusal(StatusCodes.Status409Conflict, "upload_already_completed", "This upload was completed or aborted by another request.");
                }

                asset = NewAsset(id, upload.Key, upload.ContentType, assembled, now);
                db.Assets.Add(asset);
                await db.UploadParts.Where(p => p.UploadId == uploadId).ExecuteDeleteAsync(cancellationToken);
                await db.Uploads.Where(u => u.UploadId == uploadId).ExecuteDeleteAsync(cancellationToken);
                db.Audit(now, actor, "asset.uploaded", id.ToString(), $"{assembled.Size} bytes in {listed.Count} parts, quarantined");
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            store.DeleteUploadParts(uploadId);
            signal.Notify();
            return AssetView.From(asset);
        }
        catch
        {
            await store.WipeAsync(path, CancellationToken.None);
            throw;
        }
    }

    public async Task AbortAsync(Guid uploadId, string actor, CancellationToken cancellationToken)
    {
        await FindUploadAsync(uploadId, cancellationToken);
        using (await gate.AcquireAsync(cancellationToken))
        {
            db.ChangeTracker.Clear();
            await db.UploadParts.Where(p => p.UploadId == uploadId).ExecuteDeleteAsync(cancellationToken);
            await db.Uploads.Where(u => u.UploadId == uploadId).ExecuteDeleteAsync(cancellationToken);
            db.Audit(time.GetUtcNow().UtcDateTime, actor, "asset.upload_aborted", uploadId.ToString());
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        store.DeleteUploadParts(uploadId);
    }

    // ---- reading ----------------------------------------------------------------------------------------

    public async Task<AssetView?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await reader.Assets.Where(a => a.Id == id).FirstOrDefaultAsync(cancellationToken) is { } row ? AssetView.From(row) : null;

    /// <summary>Only a <b>clean</b> asset can be served; anything else is the same "not found" as an id that never existed.</summary>
    public async Task<ServableAsset?> FindServableAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await reader.Assets.Where(a => a.Id == id && a.State == AssetState.Clean).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var path = store.BlobPath(row.Sha256);
        return store.Exists(path)
            ? new ServableAsset(path, row.DetectedType ?? "application/octet-stream", row.Key, row.Sha256, row.Size, row.CreatedAt)
            : null;
    }

    public async Task<IReadOnlyList<AssetView>> ListAsync(AssetState? state, int limit, CancellationToken cancellationToken)
    {
        var query = state is { } s ? reader.Assets.Where(a => a.State == s) : reader.Assets;
        var rows = await query.OrderBy(a => a.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        return [.. rows.Select(AssetView.From)];
    }

    // ---- admin ------------------------------------------------------------------------------------------

    /// <summary>Hard delete: the row goes, the bytes are overwritten and removed (unless another asset shares the content), and the audit log says who.</summary>
    public async Task<bool> DeleteAsync(Guid id, string admin, CancellationToken cancellationToken)
    {
        string? wipe = null;
        string? quarantine = null;
        using (await gate.AcquireAsync(cancellationToken))
        {
            db.ChangeTracker.Clear();
            var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (asset is null)
            {
                return false;
            }

            quarantine = store.QuarantinePath(id);
            db.Assets.Remove(asset);
            db.Audit(time.GetUtcNow().UtcDateTime, admin, "asset.deleted", id.ToString(), $"was {asset.State}");
            await db.SaveChangesAsync(cancellationToken);
            var shared = await db.Assets.AnyAsync(a => a.Sha256 == asset.Sha256 && a.State != AssetState.Rejected && a.Id != id, cancellationToken);
            wipe = shared ? null : store.BlobPath(asset.Sha256);
            db.ChangeTracker.Clear();
        }

        await store.WipeAsync(quarantine, cancellationToken);
        if (wipe is not null)
        {
            await store.WipeAsync(wipe, cancellationToken);
        }

        return true;
    }

    /// <summary>Puts a clean asset back in quarantine for another scan (for example after the scanner's rules changed). It is not served until it is cleared again.</summary>
    public Task<bool> RescanAsync(Guid id, string admin, CancellationToken cancellationToken) =>
        RequeueAsync(id, admin, "asset.rescan", a => a.State == AssetState.Clean, cancellationToken);

    /// <summary>Puts a set-aside asset back in the queue with its attempts reset.</summary>
    public Task<bool> RequeueFailedAsync(Guid id, string admin, CancellationToken cancellationToken) =>
        RequeueAsync(id, admin, "asset.requeued", a => a.State == AssetState.Failed, cancellationToken);

    /// <summary>Re-queues every clean asset that was cleared by a different scanner version. Returns how many.</summary>
    public async Task<int> RescanOutdatedAsync(string currentVersion, string admin, CancellationToken cancellationToken)
    {
        int count;
        using (await gate.AcquireAsync(cancellationToken))
        {
            db.ChangeTracker.Clear();
            var now = time.GetUtcNow().UtcDateTime;
            count = await db.Assets
                .Where(a => a.State == AssetState.Clean && a.ScannerVersion != currentVersion)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(a => a.State, AssetState.Quarantined).SetProperty(a => a.Attempts, 0).SetProperty(a => a.NextAttemptAt, now),
                    cancellationToken);
            db.Audit(now, admin, "asset.rescan_outdated", currentVersion, $"{count} assets");
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        if (count > 0)
        {
            signal.Notify();
        }

        return count;
    }

    private async Task<bool> RequeueAsync(
        Guid id, string admin, string action, System.Linq.Expressions.Expression<Func<AssetRow, bool>> eligible, CancellationToken cancellationToken)
    {
        using (await gate.AcquireAsync(cancellationToken))
        {
            db.ChangeTracker.Clear();
            var now = time.GetUtcNow().UtcDateTime;
            var changed = await db.Assets.Where(a => a.Id == id).Where(eligible)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(a => a.State, AssetState.Quarantined).SetProperty(a => a.Attempts, 0)
                        .SetProperty(a => a.NextAttemptAt, now).SetProperty(a => a.LastError, (string?)null),
                    cancellationToken);
            if (changed == 0)
            {
                return false;
            }

            db.Audit(now, admin, action, id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        signal.Notify();
        return true;
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private async Task<UploadRow> FindUploadAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await reader.Uploads.FirstOrDefaultAsync(u => u.UploadId == uploadId, cancellationToken);
        if (upload is null || upload.ExpiresAt <= time.GetUtcNow().UtcDateTime)
        {
            throw new AssetRefusal(StatusCodes.Status404NotFound, "upload_not_found", "No such upload (it may have expired).");
        }

        return upload;
    }

    private async Task EnsureRoomAsync(long incoming, CancellationToken cancellationToken)
    {
        if (await UsedBytesAsync(cancellationToken) + incoming > Settings.MaxBytes)
        {
            throw Full();
        }
    }

    private AssetRefusal Full()
    {
        logger.LogWarning("An asset was refused: the store is at its {MaxBytes} byte cap", Settings.MaxBytes);
        return new AssetRefusal(StatusCodes.Status429TooManyRequests, "assets_full", "The asset store is full. Nothing was stored; retry later.");
    }

    /// <summary>Bytes held: every asset that still has content, plus parts waiting to be assembled.</summary>
    private async Task<long> UsedBytesAsync(CancellationToken cancellationToken) =>
        await db.Assets.Where(a => a.State != AssetState.Rejected).SumAsync(a => a.Size, cancellationToken)
        + await db.UploadParts.SumAsync(p => p.Size, cancellationToken);

    private static AssetRow NewAsset(Guid id, string key, string contentType, StoredFile stored, DateTime now) =>
        new()
        {
            Id = id,
            Key = key,
            ContentType = contentType,
            Size = stored.Size,
            Sha256 = stored.Sha256,
            State = AssetState.Quarantined,
            CreatedAt = now,
            NextAttemptAt = now,
        };

    private static string ValidKey(string? key)
    {
        var text = key?.Trim() ?? "";
        if (text.Length is 0 or > 200 || text.Any(c => char.IsControl(c) || c is '/' or '\\'))
        {
            throw new AssetRefusal(StatusCodes.Status400BadRequest, "invalid_key", "A key is 1 to 200 characters with no control characters, '/' or '\\'.");
        }

        return text;
    }

    private static string ContentTypeOrDefault(string? contentType) =>
        contentType is { Length: > 0 and <= 100 } && MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed.MediaType is { Length: > 0 }
            ? parsed.MediaType
            : "application/octet-stream";

    private static void RequireChecksum(string? expected, string actual)
    {
        if (expected is not null && !string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssetRefusal(StatusCodes.Status400BadRequest, "checksum_mismatch", "The SHA-256 you sent does not match the content received.");
        }
    }
}
