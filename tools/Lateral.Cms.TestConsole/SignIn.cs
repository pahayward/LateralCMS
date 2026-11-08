using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.TestConsole;

/// <summary>
/// A sign-in in front of the whole console. The console holds the CMS credentials and acts with them, so nobody reaches any of it
/// without a session. Two demo people (set in <c>Console:SignIn:People</c>): an <c>admin</c> who may use everything, and a
/// <c>user</c> who may use the photo library only (the pages, the pictures and the handful of CMS calls the library makes).
/// The session is a signed, expiring, HttpOnly, SameSite=Strict cookie; the signing key is random per process, so a restart
/// signs everyone out. Passwords are compared in constant time and repeated failures lock a name out for a minute.
/// </summary>
public static class SignIn
{
    public const string CookieName = "lateral_console";
    public const string AdminRole = "admin";
    public const string UserRole = "user";

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Since)> Failures = new();
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SessionFor = TimeSpan.FromHours(8);

    // Pages and files anyone may fetch without a session: the sign-in page itself.
    private static readonly HashSet<string> Public = new(StringComparer.OrdinalIgnoreCase)
    {
        "/login", "/login.html", "/login.js", "/base.js", "/site.css", "/api/login", "/api/session", "/favicon.ico",
    };

    // What the photo-library person may fetch besides pictures: the page and its own scripts and styles.
    private static readonly HashSet<string> PhotoFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/index.html", "/photos.css", "/photos-lib.js", "/photos-store.js", "/photos-app.js", "/sha256.js", "/session.js", "/base.js",
        "/api/config", "/api/logout",
    };

    // The CMS calls the photo library makes (method, path under /api/cms/), each tied to the ONE account the library makes it
    // as. Nothing else is proxied for that person, and no call may be made as a different account: otherwise the library login
    // would be an admin login (read unpublished records, delete any file) or a CMS login (publish and delete anything).
    private static readonly (string Account, Regex Call)[] PhotoCmsCalls =
    [
        ("user", new(@"^GET api/entities(/[^/]+)?$", RegexOptions.Compiled)),
        ("cms", new(@"^POST cms/events$", RegexOptions.Compiled)),
        ("cms", new(@"^GET cms/batches/[0-9a-fA-F-]+$", RegexOptions.Compiled)),
        ("cms", new(@"^GET cms/assets/[0-9a-fA-F-]+$", RegexOptions.Compiled)),
        ("cms", new(@"^PUT cms/assets/[^/]+$", RegexOptions.Compiled)),
        ("cms", new(@"^POST cms/assets/uploads$", RegexOptions.Compiled)),
        ("cms", new(@"^PUT cms/assets/uploads/[0-9a-fA-F-]+/parts/\d+$", RegexOptions.Compiled)),
        ("cms", new(@"^POST cms/assets/uploads/[0-9a-fA-F-]+/complete$", RegexOptions.Compiled)),
        ("cms", new(@"^DELETE cms/assets/uploads/[0-9a-fA-F-]+$", RegexOptions.Compiled)),
        ("admin", new(@"^DELETE api/assets/[0-9a-fA-F-]+$", RegexOptions.Compiled)),
    ];

    public static IEndpointRouteBuilder MapSignIn(this IEndpointRouteBuilder app)
    {
        app.MapGet("/login", (IWebHostEnvironment env) => Results.Stream(env.WebRootFileProvider.GetFileInfo("login.html").CreateReadStream(), "text/html; charset=utf-8"));
        app.MapPost("/api/login", Login);
        app.MapPost("/api/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath(context), SameSite = SameSiteMode.Strict });
            return Results.Ok(new { signedOut = true });
        });
        app.MapGet("/api/session", (HttpContext context, IOptions<ConsoleOptions> options) =>
            !options.Value.SignIn.Enabled ? Results.Ok(new { name = "open", role = AdminRole, enabled = false })
            : Read(context) is { } session ? Results.Ok(new { name = session.Name, role = session.Role, enabled = true })
            : Results.Json(new { error = "Not signed in." }, statusCode: StatusCodes.Status401Unauthorized));
        return app;
    }

    public sealed record Session(string Name, string Role);

    private sealed record LoginRequest(string? Username, string? Password);

    private static IResult Login(LoginRequest body, HttpContext context, IOptions<ConsoleOptions> options)
    {
        var name = (body.Username ?? "").Trim().ToLowerInvariant();
        if (Failures.TryGetValue(name, out var state) && state.Failures >= MaxFailures && DateTimeOffset.UtcNow - state.Since < LockFor)
        {
            return Results.Json(new { error = "Too many wrong passwords. Wait a minute and try again." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        var person = options.Value.SignIn.People.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        // Always compare something, so a wrong name takes as long as a wrong password.
        var wanted = Encoding.UTF8.GetBytes(person?.Password ?? Guid.NewGuid().ToString());
        var given = Encoding.UTF8.GetBytes(body.Password ?? "");
        var match = CryptographicOperations.FixedTimeEquals(SHA256.HashData(wanted), SHA256.HashData(given)) && person is not null;
        if (!match)
        {
            Failures.AddOrUpdate(name, _ => (1, DateTimeOffset.UtcNow),
                (_, old) => DateTimeOffset.UtcNow - old.Since < LockFor ? (old.Failures + 1, old.Since) : (1, DateTimeOffset.UtcNow));
            return Results.Json(new { error = "That name and password do not match." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        Failures.TryRemove(name, out _);
        var expires = DateTimeOffset.UtcNow.Add(SessionFor);
        context.Response.Cookies.Append(CookieName, Sign(person!.Name, person.Role, expires), new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, Path = CookiePath(context), Expires = expires,
        });
        return Results.Ok(new { name = person.Name, role = person.Role });
    }

    /// <summary>The session cookie belongs to this site only: under a prefix, it is not sent to whatever else shares the address.</summary>
    private static string CookiePath(HttpContext context) => context.Request.PathBase.HasValue ? context.Request.PathBase.Value! : "/";

    private static string Sign(string name, string role, DateTimeOffset expires)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{name}|{role}|{expires.ToUnixTimeSeconds()}"));
        return payload + "." + Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(payload)));
    }

    public static Session? Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var cookie) || cookie.Split('.') is not [var payload, var mac])
        {
            return null;
        }

        var expected = Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(payload)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(mac.ToUpperInvariant())))
        {
            return null;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(payload)).Split('|');
            return parts.Length == 3 && long.Parse(parts[2]) > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? new Session(parts[0], parts[1]) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Nothing is served without a session; the photo-library person gets only the photo library.</summary>
    public static IApplicationBuilder UseSignIn(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<ConsoleOptions>>().Value;
            var path = context.Request.Path.Value ?? "/";
            if (!options.SignIn.Enabled || Public.Contains(path))
            {
                await next();
                return;
            }

            var session = Read(context);
            if (session is null)
            {
                if (context.Request.Method == "GET" && !path.StartsWith("/api/", StringComparison.Ordinal) && !path.StartsWith("/media/", StringComparison.Ordinal))
                {
                    context.Response.Redirect(context.Request.PathBase + "/login?next=" + Uri.EscapeDataString(path));
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Sign in first." });
                }

                return;
            }

            if (session.Role != AdminRole && Refusal(context, path) is { } reason)
            {
                if (context.Request.Method == "GET" && !path.StartsWith("/api/", StringComparison.Ordinal) && !path.StartsWith("/media/", StringComparison.Ordinal))
                {
                    context.Response.Redirect(context.Request.PathBase.HasValue ? context.Request.PathBase + "/" : "/");
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = reason });
                return;
            }

            await next();
        });

    private static string? Refusal(HttpContext context, string path)
    {
        if (PhotoFiles.Contains(path) || path.StartsWith("/media/", StringComparison.Ordinal))
        {
            return null;
        }

        if (path.StartsWith("/api/cms/", StringComparison.Ordinal))
        {
            var actAs = context.Request.Headers["X-Console-Act-As"].ToString().Trim().ToLowerInvariant();
            var call = $"{context.Request.Method} {path["/api/cms/".Length..]}";
            return PhotoCmsCalls.Any(c => c.Account == actAs && c.Call.IsMatch(call))
                ? null
                : "The photo library may not make that call. Sign in as admin for the engineering console.";
        }

        return "The photo library sign-in cannot open this. Sign in as admin for the engineering console.";
    }
}
