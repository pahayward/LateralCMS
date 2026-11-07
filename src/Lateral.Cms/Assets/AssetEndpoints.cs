using System.Security.Claims;
using System.Threading.RateLimiting;
using Lateral.Cms.Auth;
using Lateral.Cms.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Lateral.Cms.Assets;

public static class AssetEndpoints
{
    public const string Sha256Header = "X-Content-SHA256";
    public const string DownloadsLimiter = "downloads";

    public static IServiceCollection AddCmsAssets(this IServiceCollection services)
    {
        services.AddOptions<AssetOptions>()
            .BindConfiguration(AssetOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AssetOptions>, AssetOptionsValidator>();

        services.AddSingleton<IAssetStore, FileAssetStore>();
        services.AddSingleton<IAssetScanner, MockAssetScanner>();
        services.AddSingleton<ScanSignal>();
        services.AddScoped<AssetService>();
        services.AddScoped<ScanProcessor>();
        services.AddScoped<AssetSweepService>();
        services.AddHostedService<ScanWorker>();
        services.AddHostedService<AssetSweeper>();
        return services;
    }

    /// <summary>Concurrent downloads are limited per caller, so one client cannot hold every file handle.</summary>
    public static void AddAssetRateLimits(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions limits) =>
        limits.AddPolicy(DownloadsLimiter, http =>
            RateLimitPartition.GetConcurrencyLimiter(
                http.User.Identity?.Name ?? "anonymous",
                _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<IOptions<AssetOptions>>().Value.MaxConcurrentDownloadsPerCaller,
                    QueueLimit = 0,
                }));

    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        // The CMS uploads.
        app.MapPut("/cms/assets/{key}", PutAsync).RequireAuthorization(Policies.CmsIngest).WithName("PutAsset");
        app.MapPost("/cms/assets/uploads", InitiateAsync).RequireAuthorization(Policies.CmsIngest).WithName("InitiateUpload");
        app.MapPut("/cms/assets/uploads/{uploadId:guid}/parts/{partNumber:int}", PutPartAsync).RequireAuthorization(Policies.CmsIngest).WithName("PutPart");
        app.MapPost("/cms/assets/uploads/{uploadId:guid}/complete", CompleteAsync).RequireAuthorization(Policies.CmsIngest).WithName("CompleteUpload");
        app.MapDelete("/cms/assets/uploads/{uploadId:guid}", AbortAsync).RequireAuthorization(Policies.CmsIngest).WithName("AbortUpload");

        // The CMS and admins follow an asset: its state, never its bytes.
        app.MapGet("/cms/assets/{id:guid}", StatusAsync).RequireAuthorization(Policies.BatchStatus).WithName("GetAssetStatus");

        // Users and admins download. Only a clean asset exists for them.
        app.MapMethods("/api/assets/{id:guid}", ["GET", "HEAD"], DownloadAsync)
            .RequireAuthorization(Policies.ApiRead)
            .RequireRateLimiting(DownloadsLimiter)
            .WithName("DownloadAsset");

        // Admins administer.
        app.MapDelete("/api/assets/{id:guid}", DeleteAsync).RequireAuthorization(Policies.AdminOnly).WithName("DeleteAsset");
        app.MapGet("/api/admin/assets", ListAsync).RequireAuthorization(Policies.AdminOnly).WithName("ListAssets");
        app.MapPost("/api/admin/assets/{id:guid}/rescan", RescanAsync).RequireAuthorization(Policies.AdminOnly).WithName("RescanAsset");
        app.MapPost("/api/admin/assets/{id:guid}/requeue", RequeueAsync).RequireAuthorization(Policies.AdminOnly).WithName("RequeueAsset");
        app.MapPost("/api/admin/assets/rescan-outdated", RescanOutdatedAsync).RequireAuthorization(Policies.AdminOnly).WithName("RescanOutdatedAssets");
        return app;
    }

    private static string Actor(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

    private static IResult Problem(AssetRefusal refusal, HttpContext http)
    {
        if (refusal.Status == StatusCodes.Status429TooManyRequests)
        {
            http.Response.Headers.RetryAfter = "10";
        }

        return Results.Problem(statusCode: refusal.Status, detail: refusal.Message, extensions: new Dictionary<string, object?> { ["code"] = refusal.Code });
    }

    private static async Task<IResult> Guard(HttpContext http, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (AssetRefusal refusal)
        {
            return Problem(refusal, http);
        }
    }

    /// <summary>Lets Kestrel accept a body up to the limit while it is being read (its default is far lower than a part).</summary>
    private static void AllowBody(HttpContext http, long bytes)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = bytes;
        }
    }

    private static Task<IResult> PutAsync(string key, HttpContext http, ClaimsPrincipal user, AssetService assets, IOptions<AssetOptions> options, CancellationToken ct)
    {
        AllowBody(http, options.Value.MaxSimpleBytes);
        return Guard(http, async () =>
        {
            var view = await assets.PutAsync(
                key, http.Request.ContentType, http.Request.ContentLength, http.Request.Headers[Sha256Header], http.Request.Body, Actor(user), ct);
            return Results.Accepted($"/cms/assets/{view.Id}", view);
        });
    }

    private static Task<IResult> InitiateAsync(InitiateRequest? request, HttpContext http, ClaimsPrincipal user, AssetService assets, CancellationToken ct) =>
        Guard(http, async () => Results.Created("/cms/assets/uploads", await assets.InitiateAsync(request, Actor(user), ct)));

    private static Task<IResult> PutPartAsync(
        Guid uploadId, int partNumber, HttpContext http, AssetService assets, IOptions<AssetOptions> options, CancellationToken ct)
    {
        AllowBody(http, options.Value.MaxPartBytes);
        return Guard(http, async () =>
        {
            var receipt = await assets.PutPartAsync(uploadId, partNumber, http.Request.ContentLength, http.Request.Headers[Sha256Header], http.Request.Body, ct);
            http.Response.Headers.ETag = $"\"{receipt.Sha256}\"";
            return Results.Ok(receipt);
        });
    }

    private static Task<IResult> CompleteAsync(
        Guid uploadId, CompleteRequest? request, HttpContext http, ClaimsPrincipal user, AssetService assets, CancellationToken ct) =>
        Guard(http, async () =>
        {
            var view = await assets.CompleteAsync(uploadId, request, Actor(user), ct);
            http.Response.Headers.Location = $"/cms/assets/{view.Id}";
            return Results.Accepted(value: view);
        });

    private static Task<IResult> AbortAsync(Guid uploadId, HttpContext http, ClaimsPrincipal user, AssetService assets, CancellationToken ct) =>
        Guard(http, async () =>
        {
            await assets.AbortAsync(uploadId, Actor(user), ct);
            return Results.NoContent();
        });

    private static async Task<IResult> StatusAsync(Guid id, AssetService assets, CancellationToken ct) =>
        await assets.GetAsync(id, ct) is { } view ? Results.Ok(view) : NotFound();

    private static async Task<IResult> DownloadAsync(Guid id, HttpContext http, AssetService assets, CancellationToken ct)
    {
        // A missing asset and one that is not clean look identical: the API never confirms that something is held back.
        if (await assets.FindServableAsync(id, ct) is not { } asset)
        {
            return NotFound();
        }

        // A range in a unit other than bytes is not understood, so it is ignored and the whole file is sent (RFC 9110, 14.2).
        if (http.Request.Headers.Range.ToString() is { Length: > 0 } range && !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            http.Request.Headers.Remove("Range");
        }

        // Whatever these bytes are, they are only ever a download: never rendered, sniffed or framed.
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return Results.File(
            asset.Path,
            asset.ContentType,
            fileDownloadName: asset.FileName,
            lastModified: new DateTimeOffset(DateTime.SpecifyKind(asset.CreatedAt, DateTimeKind.Utc)),
            entityTag: new EntityTagHeaderValue($"\"{asset.Sha256}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal user, AssetService assets, CancellationToken ct) =>
        await assets.DeleteAsync(id, Actor(user), ct) ? Results.NoContent() : NotFound();

    private static async Task<IResult> ListAsync(AssetState? state, int? limit, AssetService assets, CancellationToken ct) =>
        Results.Ok(await assets.ListAsync(state, limit ?? 50, ct));

    private static async Task<IResult> RescanAsync(Guid id, ClaimsPrincipal user, AssetService assets, CancellationToken ct) =>
        await assets.RescanAsync(id, Actor(user), ct)
            ? Results.Ok(new { id, state = AssetState.Quarantined })
            : Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Only a clean asset can be re-scanned (or it does not exist).",
                extensions: new Dictionary<string, object?> { ["code"] = "asset_not_clean" });

    private static async Task<IResult> RequeueAsync(Guid id, ClaimsPrincipal user, AssetService assets, CancellationToken ct) =>
        await assets.RequeueFailedAsync(id, Actor(user), ct)
            ? Results.Ok(new { id, state = AssetState.Quarantined })
            : Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Only an asset whose scanning was set aside can be requeued (or it does not exist).",
                extensions: new Dictionary<string, object?> { ["code"] = "asset_not_failed" });

    private static async Task<IResult> RescanOutdatedAsync(ClaimsPrincipal user, AssetService assets, IAssetScanner scanner, CancellationToken ct) =>
        Results.Ok(new { scannerVersion = scanner.Version, queued = await assets.RescanOutdatedAsync(scanner.Version, Actor(user), ct) });

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "No such asset.", extensions: new Dictionary<string, object?> { ["code"] = "asset_not_found" });
}
