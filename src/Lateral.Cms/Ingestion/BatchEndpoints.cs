using System.Security.Claims;
using Lateral.Cms.Auth;
using Lateral.Cms.Domain;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Ingestion;

/// <summary>What a failed batch looked like when the worker gave up. Never any payload.</summary>
public sealed record FailedBatchInfo(Guid BatchId, DateTime ReceivedAt, int Events, int Attempts, string? LastError);

/// <summary>Body of <c>GET /api/admin/ingestion</c>: whether the queue is keeping up.</summary>
public sealed record QueueReport(
    bool WorkerEnabled,
    int PendingBatches,
    int PendingEvents,
    double? OldestPendingAgeSeconds,
    int FailedBatches,
    long FailedBytes,
    long UsedBytes,
    long MaxBytes,
    IReadOnlyList<FailedBatchInfo> Failed);

/// <summary>Reads where a batch has got to. Reads only, from the read-only context.</summary>
public sealed class BatchStatusQueries(CmsReadDbContext db, TimeProvider time, IOptions<InboxOptions> inbox)
{
    /// <summary>The batch as the sender sees it, or null if nothing was ever recorded under that id.</summary>
    public async Task<IngestResult?> GetAsync(Guid batchId, CancellationToken cancellationToken)
    {
        // One snapshot, so a batch the worker finishes mid-read is never seen half queued and half decided.
        await using var snapshot = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await db.Batches
            .Where(b => b.BatchId == batchId)
            .Select(b => new { b.Status, b.ReceivedAt, b.CompletedAt, b.Attempts, b.LastError })
            .FirstOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            return null;
        }

        // The payload is not read: the sender gets what it needs to follow its own events, not the content back.
        var results = await db.Events
            .Where(e => e.BatchId == batchId)
            .OrderBy(e => e.Sequence)
            .Select(e => new EventResult(e.Sequence, e.EntityId, e.RawType, e.Version, e.Outcome, e.Reason, e.PayloadChecksum))
            .ToListAsync(cancellationToken);

        var retried = batch.Attempts > 0;
        return IngestResult.From(
            batchId, batch.Status, results, batch.ReceivedAt, batch.CompletedAt,
            attempts: retried ? batch.Attempts : null, lastError: retried ? batch.LastError : null);
    }

    /// <summary>Queue depth, age, and any batches set aside.</summary>
    public async Task<QueueReport> ReportAsync(CancellationToken cancellationToken)
    {
        var stats = await QueueStats.ReadAsync(db.Batches, cancellationToken);
        var failed = await db.Batches
            .Where(b => b.Status == BatchState.Failed)
            .OrderBy(b => b.Id)
            .Take(50)
            .Select(b => new FailedBatchInfo(b.BatchId, b.ReceivedAt, b.EventCount, b.Attempts, b.LastError))
            .ToListAsync(cancellationToken);

        var age = stats.OldestPending is { } oldest
            ? Math.Round(Math.Max(0, (time.GetUtcNow().UtcDateTime - oldest).TotalSeconds), 1)
            : (double?)null;
        return new QueueReport(
            inbox.Value.WorkerEnabled, stats.PendingBatches, stats.PendingEvents, age, stats.FailedBatches, stats.FailedBytes, stats.UsedBytes,
            inbox.Value.MaxBytes, failed);
    }
}

/// <summary>Puts a failed batch back in the queue.</summary>
public sealed class QueueAdminService(
    CmsWriteDbContext db, WriteGate gate, WakeSignal signal, TimeProvider time, ILogger<QueueAdminService> logger)
{
    public enum RequeueResult
    {
        Requeued,
        NotFound,
        NotFailed,
    }

    public async Task<RequeueResult> RequeueAsync(Guid batchId, string admin, CancellationToken cancellationToken)
    {
        using var _ = await gate.AcquireAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var batch = await db.Batches.FirstOrDefaultAsync(b => b.BatchId == batchId, cancellationToken);
        if (batch is null)
        {
            return RequeueResult.NotFound;
        }

        if (batch.Status != BatchState.Failed)
        {
            return RequeueResult.NotFailed;
        }

        batch.Status = BatchState.Queued;
        batch.Attempts = 0;
        batch.LastError = null;
        batch.NextAttemptAt = time.GetUtcNow().UtcDateTime;
        db.Audit(batch.NextAttemptAt, admin, "batch.requeued", batchId.ToString());
        await db.SaveChangesAsync(cancellationToken);

        signal.Notify();
        IngestionLog.AdminRequeue(logger, admin, batchId);
        return RequeueResult.Requeued;
    }
}

public static class BatchEndpoints
{
    public static IServiceCollection AddCmsQueue(this IServiceCollection services)
    {
        services.AddOptions<InboxOptions>()
            .BindConfiguration(InboxOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<InboxOptions>, InboxOptionsValidator>();

        services.AddSingleton<WakeSignal>();
        services.AddSingleton<QueueBacklog>();
        services.AddScoped<AcceptService>();
        services.AddScoped<BatchProcessor>();
        services.AddScoped<BatchStatusQueries>();
        services.AddScoped<QueueAdminService>();
        services.AddHostedService<BatchWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapBatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/cms/batches/{batchId:guid}", GetBatchAsync)
            .RequireAuthorization(Policies.BatchStatus)
            .WithName("GetBatchStatus");

        app.MapGet("/api/admin/ingestion", ReportAsync)
            .RequireAuthorization(Policies.AdminOnly)
            .WithName("GetIngestionStatus");

        app.MapPost("/api/admin/ingestion/{batchId:guid}/requeue", RequeueAsync)
            .RequireAuthorization(Policies.AdminOnly)
            .WithName("RequeueFailedBatch");

        return app;
    }

    private static async Task<IResult> GetBatchAsync(Guid batchId, BatchStatusQueries queries, CancellationToken cancellationToken) =>
        await queries.GetAsync(batchId, cancellationToken) is { } batch
            ? Results.Ok(batch)
            : Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                detail: "No batch with that id was recorded.",
                extensions: new Dictionary<string, object?> { ["code"] = "batch_not_found" });

    private static async Task<IResult> ReportAsync(BatchStatusQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.ReportAsync(cancellationToken));

    private static async Task<IResult> RequeueAsync(
        Guid batchId, ClaimsPrincipal user, QueueAdminService admin, CancellationToken cancellationToken)
    {
        var result = await admin.RequeueAsync(batchId, user.Identity?.Name ?? "unknown", cancellationToken);
        return result switch
        {
            QueueAdminService.RequeueResult.Requeued => Results.Ok(new { batchId, status = BatchState.Queued }),
            QueueAdminService.RequeueResult.NotFailed => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                detail: "Only a batch that has failed can be requeued.",
                extensions: new Dictionary<string, object?> { ["code"] = "batch_not_failed" }),
            _ => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                detail: "No batch with that id.",
                extensions: new Dictionary<string, object?> { ["code"] = "batch_not_found" }),
        };
    }
}
