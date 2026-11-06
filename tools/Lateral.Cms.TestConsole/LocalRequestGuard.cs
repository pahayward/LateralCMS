namespace Lateral.Cms.TestConsole;

/// <summary>
/// The console holds the CMS credentials and acts with them, so any web page that could make the browser call
/// it would borrow them. These checks stop that: the request must be addressed to a loopback name (defeats DNS
/// rebinding), must not come from another site, and must carry a header a cross-site form cannot set.
/// There is deliberately no CORS configuration, so browsers also block scripts on other origins from reading answers.
/// </summary>
public static class LocalRequestGuard
{
    public const string RequestHeader = "X-Console-Request";

    private static readonly string[] LoopbackNames = ["localhost", "127.0.0.1", "[::1]", "::1"];

    public static IApplicationBuilder UseLocalRequestGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && Refusal(context.Request) is { } reason)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = reason });
                return;
            }

            await next();
        });

    /// <summary>Why the request must be refused, or null if it may proceed.</summary>
    public static string? Refusal(HttpRequest request)
    {
        if (!LoopbackNames.Contains(request.Host.Host, StringComparer.OrdinalIgnoreCase))
        {
            return "The console only answers requests addressed to localhost.";
        }

        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none"))
        {
            return "Cross-site requests are refused.";
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0
            && (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                || !string.Equals(parsed.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return "The request origin does not match the console.";
        }

        return request.Headers[RequestHeader] == "1" ? null : $"Missing the {RequestHeader} header.";
    }

    /// <summary>Headers for every response: the UI needs only its own scripts and styles and the console's own API.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; " +
                "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                headers.CacheControl = "no-store";
            }

            await next();
        });
}
