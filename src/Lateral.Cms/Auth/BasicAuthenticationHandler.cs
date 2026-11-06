using System.Net.Http.Headers;
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
    AuthFailureThrottle throttle)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Basic";
    private const string RetryAfterItem = "Lateral.Auth.RetryAfter";
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out var header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!AuthenticationHeaderValue.TryParse(header.ToString(), out var parsed)
            || !SchemeName.Equals(parsed.Scheme, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(parsed.Parameter))
        {
            // Some other scheme, or an empty credential: we have nothing to authenticate.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var client = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsBlocked(client, out var retryAfter))
        {
            Context.Items[RetryAfterItem] = retryAfter;
            return Task.FromResult(AuthenticateResult.Fail("Too many failed attempts."));
        }

        if (!TryDecode(parsed.Parameter, out var username, out var password)
            || credentials.Validate(username, password) is not { } principal)
        {
            throttle.RecordFailure(client);
            // The username is deliberately not logged: it is attacker-controlled text.
            Logger.LogWarning("Basic authentication failed for {Method} {Path} from {Client}", Request.Method, Request.Path, client);
            return Task.FromResult(AuthenticateResult.Fail("Invalid credentials."));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"{SchemeName} realm=\"Lateral CMS\", charset=\"UTF-8\"";

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
