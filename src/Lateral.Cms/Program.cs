using System.Text.Json;
using System.Text.Json.Serialization;
using Lateral.Cms.Admin;
using Lateral.Cms.ApiDocs;
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

// "admin <command>" is the operator's tool, not the web service: same configuration and database, no server, and the command's
// words are not configuration (a "--role" must not be read as a setting).
var operatorCommand = AdminCli.IsAdminCommand(args);
if (operatorCommand && AdminCli.RunWithoutService(args, Console.Out) is { } finished)
{
    // Help, or a new service secret: no configuration or database needed (a fresh install has neither yet).
    return finished;
}

var builder = WebApplication.CreateBuilder(operatorCommand ? [] : args);
if (operatorCommand)
{
    // The tool speaks for itself; the service's log lines would only get in the way. (A rule, not a minimum: the
    // configuration's own default rule would otherwise win.)
    builder.Logging.AddFilter(level => level >= LogLevel.Warning);
}

// Development only, and only if nothing else supplied credentials: generate them (git-ignored) instead of committing any.
DevelopmentCredentials.AddIfDevelopment(builder);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HealthProbe>();
builder.Services.AddProblemDetails();

// Do not announce what is serving: no "Server: Kestrel" header for anyone to read.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

// A query value or body that cannot be read is the caller's mistake: a 400 with a problem body. ASP.NET's default in the
// Development environment is to throw instead, which the exception handler turns into a 500, so say it plainly.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
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
builder.Services.AddCmsAdminInfo();
builder.Services.AddCmsAssets();
builder.Services.AddCmsApiDocs();
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

if (operatorCommand)
{
    return await AdminCli.RunAsync(app.Services, args, Console.Out, Console.Error, Console.In);
}

// Nobody can sign in yet (a fresh install): make the first administrator and say where its temporary password is.
await AccountBootstrap.RunAsync(app);

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

app.UseLoginBodyLimit();
app.UseAuthentication();
app.UsePasswordChangeRequired();
app.UseAuthorization();
app.UseRateLimiter();

// Swagger: the API reference at /swagger, for a signed-in caller.
app.UseCmsApiDocs();

app.MapAccountEndpoints();
app.MapIngestionEndpoints();
app.MapBatchEndpoints();
app.MapAssetEndpoints();
app.MapEntityEndpoints();
app.MapAdminEndpoints();
app.MapAdminInfoEndpoints();

app.MapGet("/health", async (HealthProbe probe, CancellationToken cancellationToken) =>
        await probe.IsUpAsync(cancellationToken)
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous()
    .WithName("Health");

app.Run();
return 0;

/// <summary>Exposed so the integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
