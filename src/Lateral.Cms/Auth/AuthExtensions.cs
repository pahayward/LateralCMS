using Lateral.Cms.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Auth;

public static class AuthExtensions
{
    public const string SessionScheme = "Session";
    public const string SmartScheme = "Smart";
    public const string SessionCookieName = "lateral.session";

    public static IServiceCollection AddCmsAuth(this IServiceCollection services)
    {
        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();

        services.AddSingleton<AccountService>();
        services.AddSingleton<InitialPasswordFile>();
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<AuthFailureThrottle>();
        services.AddSingleton<CmsAddressRule>();

        // Session cookies are signed with keys kept on disk, so a restart does not sign everybody out.
        services.AddDataProtection().SetApplicationName("Lateral.Cms");
        services.AddOptions<KeyManagementOptions>().Configure<DatabaseConnections, IOptions<AuthOptions>, ILoggerFactory>(
            (keys, database, auth, loggers) =>
            {
                var folder = auth.Value.KeysPath is { Length: > 0 } configured
                    ? Path.GetFullPath(configured)
                    : Path.Combine(Path.GetDirectoryName(database.DataSource)!, "keys");
                Directory.CreateDirectory(folder);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(folder), loggers);
            });

        services.AddAuthentication(SmartScheme)
            // Scripts send Basic credentials; the browser sends the session cookie. The header, if there is one, decides.
            .AddPolicyScheme(SmartScheme, null, scheme =>
                scheme.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey("Authorization") || !context.Request.Cookies.ContainsKey(SessionCookieName)
                        ? BasicAuthenticationHandler.SchemeName
                        : SessionScheme)
            .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(BasicAuthenticationHandler.SchemeName, null)
            .AddCookie(SessionScheme, cookie =>
            {
                cookie.Cookie.Name = SessionCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = true;
                // An API answers "not signed in" with a status, never with a redirect to a page.
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                cookie.Events.OnValidatePrincipal = SessionRules.ValidateAsync;
            });
        services.AddOptions<CookieAuthenticationOptions>(SessionScheme).Configure<IOptions<AuthOptions>>(
            (cookie, auth) => cookie.ExpireTimeSpan = TimeSpan.FromHours(auth.Value.SessionHours));

        services.AddAuthorizationBuilder()
            // Deny by default: every endpoint needs a signed-in caller unless it says otherwise.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.CmsIngest, policy => policy.RequireRole(Roles.CmsIngest))
            .AddPolicy(Policies.ApiRead, policy => policy.RequireRole(Roles.User, Roles.Admin))
            .AddPolicy(Policies.AdminOnly, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(Policies.BatchStatus, policy => policy.RequireRole(Roles.CmsIngest, Roles.Admin));

        return services;
    }

    /// <summary>
    /// A person whose password is a temporary one may do one thing: choose a new one (and look at who they are, or sign out). Everything
    /// else answers 403 until they have, for a session and for Basic credentials alike.
    /// </summary>
    public static IApplicationBuilder UsePasswordChangeRequired(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.HasClaim(AccountClaims.MustChange, "true") && !context.Request.Path.StartsWithSegments("/api/auth"))
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    detail: "This account has a temporary password. Choose a new one with POST /api/auth/password first.",
                    extensions: new Dictionary<string, object?> { ["code"] = "password_change_required" })
                    .ExecuteAsync(context);
                return;
            }

            await next();
        });
}

/// <summary>What a session cookie must satisfy on every request, beyond being a cookie the service signed.</summary>
public static class SessionRules
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var http = context.HttpContext;

        // Cross-site request forgery: a cookie travels with any request the browser makes, including one a hostile page
        // causes. SameSite=Strict already keeps it from being sent; this also requires a header that a hostile page cannot add
        // without the service's agreement. Reading needs nothing extra.
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method)
            && !http.Request.Headers.ContainsKey(AccountClaims.ClientHeader))
        {
            context.RejectPrincipal();
            return;
        }

        var principal = context.Principal!;
        var name = principal.Identity?.Name ?? "";
        if (principal.FindFirst(AccountClaims.Source)?.Value == "config")
        {
            // A configured account has no record to check; it must still be configured, with the same role.
            var store = http.RequestServices.GetRequiredService<CredentialStore>();
            var role = principal.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
            if (!store.ConfiguredAccounts.Any(a => a.Username == name && a.Role == role && a.Role != Roles.CmsIngest))
            {
                context.RejectPrincipal();
                await http.SignOutAsync(AuthExtensions.SessionScheme);
            }

            return;
        }

        var accounts = http.RequestServices.GetRequiredService<AccountService>();
        var person = Guid.TryParse(principal.FindFirst(AccountClaims.UserId)?.Value, out var id)
            ? await accounts.GetAsync(id, http.RequestAborted)
            : null;
        if (person is null || person.Disabled || person.Stamp.ToString() != principal.FindFirst(AccountClaims.Stamp)?.Value)
        {
            context.RejectPrincipal();
            await http.SignOutAsync(AuthExtensions.SessionScheme);
            return;
        }

        // Role and "must change password" are read afresh, so an administrator's change applies to a session already open.
        context.ReplacePrincipal(AccountClaims.Principal(person, AuthExtensions.SessionScheme));
    }
}
