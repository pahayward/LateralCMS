using Microsoft.Extensions.Options;

namespace Lateral.Cms.TestConsole;

/// <summary>
/// Pictures for the photo library. A browser's <c>&lt;img&gt;</c> cannot send an Authorization header, so the page asks the
/// console for <c>/media/{id}</c> and the console fetches the asset from the CMS as the library's reader account, the same
/// way the proxy does for everything else. Only the asset download route is reachable this way, only by id, and only to read.
/// Assets never change once stored, so a picture is cached for good.
/// </summary>
public static class MediaEndpoints
{
    private const int Attempts = 4;

    // What a ranged, cacheable, saveable picture needs from the CMS's answer; nothing else is passed on.
    private static readonly string[] ForwardedResponseHeaders = ["Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Content-Length"];
    private static readonly string[] ForwardedRequestHeaders = ["Range", "If-Range", "If-None-Match"];

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/media/{id:guid}", ["GET", "HEAD"], Media);
        return app;
    }

    private static async Task Media(
        Guid id, string? download, HttpContext context, IHttpClientFactory clients, AccountBook accounts, IOptions<ConsoleOptions> options)
    {
        if (accounts.BasicHeader(AccountBook.User) is not { } authorization)
        {
            await Fail(context, StatusCodes.Status503ServiceUnavailable, "The console has no reader account configured.");
            return;
        }

        if (!CmsTarget.TryBuild(new Uri(options.Value.CmsBaseUrl), $"api/assets/{id}", null, out var target, out var error))
        {
            await Fail(context, StatusCodes.Status400BadRequest, error);
            return;
        }

        try
        {
            // The CMS limits how many downloads one caller has going at once and answers 429 beyond it. A page of thumbnails
            // can briefly ask for more than that, so a refused picture is asked for again a moment later.
            HttpResponseMessage? response = null;
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
                foreach (var name in ForwardedRequestHeaders)
                {
                    if (context.Request.Headers.TryGetValue(name, out var values))
                    {
                        request.Headers.TryAddWithoutValidation(name, values.ToArray());
                    }
                }

                response = await clients.CreateClient("cms").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == Attempts)
                {
                    break;
                }

                response.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(120 * attempt), context.RequestAborted);
            }

            using (response)
            {
                context.Response.StatusCode = (int)response!.StatusCode;
                var ok = (int)response.StatusCode is 200 or 206 or 304;
                if (response.Content.Headers.ContentType is { } type)
                {
                    context.Response.ContentType = type.ToString();
                }

                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (ForwardedResponseHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                context.Response.Headers.CacheControl = ok ? "private, max-age=31536000, immutable" : "no-store";

                // Shown in the page as a picture, or saved as a file when the page says so (the CMS names the file).
                if (download is "1" or "true" && response.Content.Headers.ContentDisposition is { } disposition)
                {
                    context.Response.Headers.ContentDisposition = disposition.ToString();
                }

                if (context.Request.Method != "HEAD")
                {
                    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
            }
        }
        catch (HttpRequestException)
        {
            await Fail(context, StatusCodes.Status502BadGateway, "Cannot reach the CMS.");
        }
    }

    private static async Task Fail(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
