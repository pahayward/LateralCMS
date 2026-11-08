using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Lateral.Cms.Auth;

public sealed class BasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    CredentialStore credentials,
    AuthFailureThrottle throttle,
    CmsAddressRule cmsAddresses)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Basic";
    private const string RetryAfterItem = "Lateral.Auth.RetryAfter";
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out var header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!AuthenticationHeaderValue.TryParse(header.ToString(), out var parsed)
            || !SchemeName.Equals(parsed.Scheme, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(parsed.Parameter))
        {
            // Some other scheme, or an empty credential: we have nothing to authenticate.
            return AuthenticateResult.NoResult();
        }

        var client = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsBlocked(client, out var retryAfter))
        {
            // The block is a brake on guessing, and the CMS service account cannot be guessed: its secret is a random GUID that
            // startup insists on, and checking it costs one hash, not a slow password hash. So a correct CMS credential still gets
            // through from a blocked address; anyone could otherwise lock the CMS out by failing ten times from its address (which
            // is every caller's address when the service sits behind a proxy that is not configured). Anything else stays blocked.
            if (TryDecode(parsed.Parameter, out var cmsName, out var cmsPassword)
                && credentials.Validate(cmsName, cmsPassword) is { } cms
                && cms.IsInRole(Roles.CmsIngest))
            {
                return FromAllowedAddress(cms) ?? AuthenticateResult.Success(new AuthenticationTicket(cms, SchemeName));
            }

            Context.Items[RetryAfterItem] = retryAfter;
            return AuthenticateResult.Fail("Too many failed attempts.");
        }

        ClaimsPrincipal? principal = null;
        if (!TryDecode(parsed.Parameter, out var username, out var password)
            || (principal = await credentials.ValidateAsync(username, password, Context.RequestAborted)) is null)
        {
            throttle.RecordFailure(client);
            // The username is deliberately not logged: it is attacker-controlled text.
            Logger.LogWarning("Basic authentication failed for {Method} {Path} from {Client}", Request.Method, Request.Path, client);
            return AuthenticateResult.Fail("Invalid credentials.");
        }

        return FromAllowedAddress(principal) ?? AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <summary>
    /// A failure if this is the CMS account and the service is told where the CMS connects from and this is somewhere else; null
    /// otherwise. The credential is right, so it is not counted as a guess: the caller is simply not where the CMS is.
    /// </summary>
    private AuthenticateResult? FromAllowedAddress(ClaimsPrincipal principal)
    {
        if (!principal.IsInRole(Roles.CmsIngest) || cmsAddresses.Allows(Context.Connection.RemoteIpAddress))
        {
            return null;
        }

        Logger.LogWarning("The CMS credential was presented from an address that is not allowed: {Client}", Context.Connection.RemoteIpAddress);
        return AuthenticateResult.Fail("Not from an allowed address.");
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // A page with its own sign-in form must not get the browser's native password box on top of it.
        if (!Request.Headers.ContainsKey(AccountClaims.ClientHeader))
        {
            Response.Headers.WWWAuthenticate = $"{SchemeName} realm=\"Lateral CMS\", charset=\"UTF-8\"";
        }

        if (Context.Items.TryGetValue(RetryAfterItem, out var value) && value is TimeSpan retryAfter)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }
        else
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Task.CompletedTask;
    }

    private static bool TryDecode(string parameter, out string username, out string password)
    {
        username = password = "";
        try
        {
            var text = StrictUtf8.GetString(Convert.FromBase64String(parameter));
            var separator = text.IndexOf(':');
            if (separator < 0)
            {
                return false;
            }

            username = text[..separator];
            password = text[(separator + 1)..];
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
