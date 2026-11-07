using System.Data.Common;
using System.Text.Json;
using Lateral.Cms.Auth;
using Lateral.Cms.Domain;
using Lateral.Cms.Integrity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Ingestion;

public static class IngestionEndpoints
{
    public const string CorrelationHeader = "X-Correlation-Id";
    public const string AcceptLimiter = "accept";

    public static IServiceCollection AddCmsIngestion(this IServiceCollection services)
    {
        services.AddOptions<IngestionOptions>()
            .BindConfiguration(IngestionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IPayloadChecksum, HashPayloadChecksum>();
        services.AddSingleton<CmsMetrics>();
        services.AddCmsQueue();
        return services;
    }

    /// <summary>Bounds how many batches are read into memory at once (see <see cref="IngestionOptions.MaxConcurrentAccepts"/>).</summary>
    public static void AddIngestionRateLimits(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions limits) =>
        limits.AddPolicy(AcceptLimiter, http =>
        {
            var settings = http.RequestServices.GetRequiredService<IOptions<IngestionOptions>>().Value;
            return System.Threading.RateLimiting.RateLimitPartition.GetConcurrencyLimiter(
                "accept",
                _ => new System.Threading.RateLimiting.ConcurrencyLimiterOptions
                {
                    PermitLimit = settings.MaxConcurrentAccepts,
                    QueueLimit = settings.AcceptQueueLimit,
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                });
        });

    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/cms/events", HandleAsync)
            .RequireAuthorization(Policies.CmsIngest)
            .RequireRateLimiting(AcceptLimiter)
            .WithName("IngestCmsEvents");
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        AcceptService accept,
        IOptions<IngestionOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Lateral.Cms.Ingestion.Endpoint");
        var settings = options.Value;

        // The id that ties this response to every log line and ledger row of the batch.
        var batchId = Guid.NewGuid();
        http.Response.Headers[CorrelationHeader] = batchId.ToString();

        // Judge the declared length first: a body announced as too big is refused without reading a byte of it.
        if (http.Request.ContentLength > settings.MaxBodyBytes)
        {
            return Refuse(logger, batchId, StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"The request body exceeds {settings.MaxBodyBytes} bytes.");
        }

        // Ask the server to enforce the limit while reading too (Kestrel does; the test server has no such feature).
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = settings.MaxBodyBytes;
        }

        byte[]? body;
        try
        {
            body = await ReadBodyAsync(http.Request, settings.MaxBodyBytes, cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            body = null;
        }

        if (body is null)
        {
            return Refuse(logger, batchId, StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"The request body exceeds {settings.MaxBodyBytes} bytes.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = settings.MaxJsonDepth });
        }
        catch (JsonException)
        {
            return Refuse(logger, batchId, StatusCodes.Status400BadRequest, "invalid_json",
                "The request body is not valid JSON (or is nested too deeply).");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Refuse(logger, batchId, StatusCodes.Status400BadRequest, "invalid_batch",
                    "The request body must be a JSON array of events.");
            }

            if (root.GetArrayLength() > settings.MaxBatchSize)
            {
                return Refuse(logger, batchId, StatusCodes.Status413PayloadTooLarge, "batch_too_large",
                    $"A batch may hold at most {settings.MaxBatchSize} events.");
            }

            try
            {
                var result = await accept.AcceptAsync(batchId, root, cancellationToken);

                // 202: stored safely, not applied yet; the Location says where to follow it. 200: nothing was left to
                // wait for, so the body already holds every final outcome.
                return result.Status == BatchState.Queued
                    ? Results.Accepted($"/cms/batches/{batchId}", result)
                    : Results.Ok(result);
            }
            catch (InboxFullException)
            {
                // Nothing was stored. Asking the sender to slow down is how a full queue stays bounded.
                http.Response.Headers.RetryAfter = "10";
                return Problem(StatusCodes.Status429TooManyRequests, "inbox_full",
                    "The service is busy and cannot take this batch yet. Nothing was stored; retry it.", batchId);
            }
            catch (Exception ex) when (ex is DbException or DbUpdateException)
            {
                // Nothing was committed, so the sender can safely retry the whole batch.
                IngestionLog.AcceptFailed(logger, ex, batchId);
                http.Response.Headers.RetryAfter = "5";
                return Problem(StatusCodes.Status503ServiceUnavailable, "storage_unavailable",
                    "The batch could not be stored. Nothing was applied; retry it.", batchId);
            }
        }
    }

    private static IResult Refuse(ILogger logger, Guid batchId, int status, string code, string detail)
    {
        IngestionLog.BatchRefused(logger, batchId, code);
        return Problem(status, code, detail, batchId);
    }

    private static IResult Problem(int status, string code, string detail, Guid batchId) =>
        Results.Problem(
            statusCode: status,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["batchId"] = batchId });

    /// <summary>Reads the body, giving up (null) as soon as it exceeds <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
