using System.Text.Json;
using System.Text.Json.Serialization;
using Lateral.Cms.Admin;
using Lateral.Cms.Assets;
using Lateral.Cms.Auth;
using Lateral.Cms.Entities;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Integrity;
using Lateral.Cms.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Development only, and only if nothing else supplied credentials: generate them (git-ignored) instead of committing any.
DevelopmentCredentials.AddIfDevelopment(builder);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    // Outcomes go over the wire as snake_case words: "applied", "ignored_stale", ...
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));

builder.Services.AddCmsAuth();
builder.Services.AddOptions<ProxyOptions>().BindConfiguration(ProxyOptions.SectionName).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ProxyOptions>, ProxyOptionsValidator>();
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ProxyOptions>>(ProxyOptions.Apply);
builder.Services.AddCmsPersistence();
builder.Services.AddCmsIngestion();
builder.Services.AddCmsEntities();
builder.Services.AddCmsAdmin();
builder.Services.AddCmsAssets();
builder.Services.AddRateLimiter(limits =>
{
    limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limits.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "5";
        return ValueTask.CompletedTask;
    };
    limits.AddAssetRateLimits();
    limits.AddIngestionRateLimits();
});

var app = builder.Build();

await app.InitialiseDatabaseAsync();

// Fail at startup, not on the first event, if the configured checksum algorithm cannot be used.
_ = app.Services.GetRequiredService<IPayloadChecksum>();

// Likewise the asset folder: create it now, so a path that cannot be written stops startup rather than the first upload.
_ = app.Services.GetRequiredService<IAssetStore>();

// Believe X-Forwarded-For only from proxies named in configuration; with none named this does nothing.
app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsProduction())
{
    // Basic auth sends the password with every request: never let it travel over plain HTTP.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    // Everything served here is confidential: nothing may be cached, sniffed or framed.
    var headers = context.Response.Headers;
    headers.CacheControl = "no-store";
    headers.XContentTypeOptions = "nosniff";
    headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapIngestionEndpoints();
app.MapBatchEndpoints();
app.MapAssetEndpoints();
app.MapEntityEndpoints();
app.MapAdminEndpoints();

app.MapGet("/health", async (CmsReadDbContext db, CancellationToken cancellationToken) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

app.Run();

/// <summary>Exposed so the integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
