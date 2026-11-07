using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.TestConsole;

public sealed record ProbeRequest(long Bytes, string? Mode, bool ExpectContinue = false);

/// <summary>
/// The console's whole API. <c>/api/cms/*</c> is a thin proxy to the CMS that adds the Authorization header for the
/// account the page chose; the rest are conveniences a browser cannot do for itself (verify a checksum
/// independently, push an oversized body without owning it).
/// </summary>
public static class ConsoleEndpoints
{
    public const string ActAsHeader = "X-Console-Act-As";
    public const string CustomCredentialsHeader = "X-Console-Custom-Credentials";

    private const long MaxProbeBytes = 16L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private static readonly string[] ProxiedMethods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"];
    private static readonly string[] ForwardedResponseHeaders =
    [
        "X-Correlation-Id", "Retry-After", "WWW-Authenticate", "Allow",
        // What a ranged, resumable download needs the page to see.
        "Content-Range", "Accept-Ranges", "ETag", "Content-Disposition", "Last-Modified", "Content-Length",
    ];

    // Request headers the page may set for assets: ranges, conditional requests and the checksum of what it sends.
    private static readonly string[] ForwardedRequestHeaders = ["Range", "If-Range", "If-None-Match", "X-Content-SHA256"];

    public static IEndpointRouteBuilder MapConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config", Config);
        app.MapMethods("/api/cms/{**path}", ProxiedMethods, Proxy);
        app.MapGet("/api/verify/{id}", Verify);
        app.MapPost("/api/probe/oversize", ProbeOversize);
        return app;
    }

    private static IResult Config(IOptions<ConsoleOptions> options, AccountBook accounts) =>
        Results.Ok(new
        {
            cmsBaseUrl = options.Value.CmsBaseUrl,
            accounts = accounts.Configured.Order().ToArray(),
            limits = options.Value.Limits,
        });

    /// <summary>The Authorization header for the account the page asked to act as, or why there is none.</summary>
    public static (string? Authorization, string? Error) ResolveAuthorization(HttpRequest request, AccountBook accounts)
    {
        var actAs = request.Headers[ActAsHeader].ToString().Trim().ToLowerInvariant();
        switch (actAs)
        {
            case "" or "anonymous":
                return (null, null);

            case "wrong":
                // A real username with a password that cannot be right.
                return (AccountBook.Basic(accounts.AnyUsername() ?? "nobody", Guid.NewGuid().ToString()), null);

            case "custom":
                var custom = request.Headers[CustomCredentialsHeader].ToString().Trim();
                return IsBase64(custom)
                    ? ("Basic " + custom, null)
                    : (null, "Custom credentials were not supplied.");

            default:
                return accounts.BasicHeader(actAs) is { } header
                    ? (header, null)
                    : (null, $"The account '{actAs}' is not configured for the console.");
        }
    }

    private static async Task Proxy(
        string path, HttpContext context, IHttpClientFactory clients, AccountBook accounts, IOptions<ConsoleOptions> options)
    {
        if (!CmsTarget.TryBuild(new Uri(options.Value.CmsBaseUrl), path, context.Request.QueryString.Value, out var target, out var error))
        {
            await Fail(context, StatusCodes.Status400BadRequest, error);
            return;
        }

        var (authorization, authError) = ResolveAuthorization(context.Request, accounts);
        if (authError is not null)
        {
            await Fail(context, StatusCodes.Status400BadRequest, authError);
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (context.Request.Headers.Accept.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Accept", context.Request.Headers.Accept.ToString());
        }

        foreach (var name in ForwardedRequestHeaders)
        {
            if (context.Request.Headers.TryGetValue(name, out var values))
            {
                request.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            var content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } contentType)
            {
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            if (context.Request.ContentLength is { } length)
            {
                content.Headers.ContentLength = length;
            }

            request.Content = content;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await clients.CreateClient("cms")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } responseType)
            {
                context.Response.ContentType = responseType.ToString();
            }

            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (ForwardedResponseHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            context.Response.Headers["X-Console-Elapsed-Ms"] = ((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds).ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            await Fail(context, StatusCodes.Status502BadGateway, $"Cannot reach the CMS at {options.Value.CmsBaseUrl}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await Fail(context, StatusCodes.Status504GatewayTimeout, "The CMS did not answer in time.");
        }
    }

    private static async Task<IResult> Verify(
        string id, string? @as, IHttpClientFactory clients, AccountBook accounts, IOptions<ConsoleOptions> options, CancellationToken cancellationToken)
    {
        var actAs = string.IsNullOrEmpty(@as) ? AccountBook.User : @as.ToLowerInvariant();
        if (actAs is not (AccountBook.User or AccountBook.Admin))
        {
            return Results.BadRequest(new { error = "Verify as 'user' or 'admin'." });
        }

        if (accounts.BasicHeader(actAs) is not { } authorization)
        {
            return Results.BadRequest(new { error = $"The account '{actAs}' is not configured for the console." });
        }

        if (!CmsTarget.TryBuild(new Uri(options.Value.CmsBaseUrl), $"api/entities/{id}", null, out var target, out var error))
        {
            return Results.BadRequest(new { error });
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        try
        {
            using var response = await clients.CreateClient("cms").SendAsync(request, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Results.Ok(new
                {
                    id,
                    status = (int)response.StatusCode,
                    visible = false,
                    error = "The CMS did not return this entity to that account (missing, unpublished and disabled look the same).",
                });
            }

            using var document = JsonDocument.Parse(bytes);
            var claimed = document.RootElement.GetProperty("checksum").GetString() ?? "";
            var payload = document.RootElement.GetProperty("payload").GetRawText();
            var check = ChecksumVerifier.Check(claimed, payload);

            return Results.Ok(new
            {
                id,
                status = 200,
                visible = true,
                version = document.RootElement.GetProperty("version").GetInt32(),
                algorithm = check.Algorithm,
                claimed = check.Claimed,
                computed = check.Computed,
                match = check.Match,
                payloadBytes = Encoding.UTF8.GetByteCount(payload),
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Results.Json(new { error = $"The CMS answer could not be checked: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Sends the CMS an ingest request whose body is <c>Bytes</c> of whitespace, either with its length declared up
    /// front or chunked, and reports what the CMS did and how much of it the sender really had to push.
    /// </summary>
    private static async Task<IResult> ProbeOversize(
        ProbeRequest probe, IHttpClientFactory clients, AccountBook accounts, IOptions<ConsoleOptions> options, CancellationToken cancellationToken)
    {
        var mode = (probe.Mode ?? "declared").ToLowerInvariant();
        if (probe.Bytes is < 1 or > MaxProbeBytes || mode is not ("declared" or "chunked"))
        {
            return Results.BadRequest(new { error = $"Send 1 to {MaxProbeBytes} bytes, with mode 'declared' or 'chunked'." });
        }

        if (accounts.BasicHeader(AccountBook.Cms) is not { } authorization)
        {
            return Results.BadRequest(new { error = "The CMS account is not configured for the console." });
        }

        if (!CmsTarget.TryBuild(new Uri(options.Value.CmsBaseUrl), "cms/events", null, out var target, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var filler = new FillerStream(probe.Bytes);
        using var content = new StreamContent(filler, 81920);
        content.Headers.ContentType = new("application/json");
        if (mode == "declared")
        {
            content.Headers.ContentLength = probe.Bytes;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.ExpectContinue = probe.ExpectContinue;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        var started = Stopwatch.GetTimestamp();
        int? status = null;
        string outcome;
        var detail = "";

        try
        {
            using var response = await clients.CreateClient("cms")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            detail = text.Length > 600 ? text[..600] + "..." : text;
            outcome = status == StatusCodes.Status413PayloadTooLarge ? "refused" : "completed";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = "timed_out";
            detail = $"The CMS was still reading after {ProbeTimeout.TotalSeconds:0} seconds, so the probe stopped.";
        }
        catch (HttpRequestException ex)
        {
            outcome = "connection_reset";
            detail = ex.InnerException?.Message ?? ex.Message;
        }

        return Results.Ok(new
        {
            requestedBytes = probe.Bytes,
            mode,
            expectContinue = probe.ExpectContinue,
            outcome,
            status,
            elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            bytesRead = filler.BytesRead,
            detail,
        });
    }

    private static Task Fail(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error });
    }

    private static bool IsBase64(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out _);
    }
}
