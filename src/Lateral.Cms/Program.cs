using System.Text.Json;
using System.Text.Json.Serialization;
using Lateral.Cms.Admin;
using Lateral.Cms.Auth;
using Lateral.Cms.Entities;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Integrity;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    // Outcomes go over the wire as snake_case words: "applied", "ignored_stale", ...
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));

builder.Services.AddCmsAuth();
builder.Services.AddCmsPersistence();
builder.Services.AddCmsIngestion();
builder.Services.AddCmsEntities();
builder.Services.AddCmsAdmin();

var app = builder.Build();

await app.InitialiseDatabaseAsync();

// Fail at startup, not on the first event, if the configured checksum algorithm cannot be used.
_ = app.Services.GetRequiredService<IPayloadChecksum>();

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

app.MapIngestionEndpoints();
app.MapBatchEndpoints();
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
