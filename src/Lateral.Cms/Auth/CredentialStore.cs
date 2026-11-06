using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Auth;

/// <summary>
/// Checks Basic-auth credentials against the configured accounts.
/// </summary>
/// <remarks>
/// Comparison is constant-time: values are hashed first (so length differences don't leak) and compared
/// with <see cref="CryptographicOperations.FixedTimeEquals"/>, and every account is always checked so the
/// response time does not reveal which usernames exist. Configuration holds the secrets in clear text for
/// simplicity; a production deployment would keep them in a secret store (see the README).
/// </remarks>
public sealed class CredentialStore
{
    private sealed record Account(string Username, string Role, byte[] UsernameHash, byte[][] PasswordHashes);

    private readonly Account[] _accounts;

    public CredentialStore(IOptions<AuthOptions> options)
    {
        var settings = options.Value;
        var accounts = new List<Account>
        {
            new(
                settings.Cms.Username,
                Roles.CmsIngest,
                Hash(settings.Cms.Username),
                settings.Cms.Passwords.Select(Hash).ToArray()),
        };

        foreach (var user in settings.Users)
        {
            var role = string.Equals(user.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase) ? Roles.Admin : Roles.User;
            accounts.Add(new Account(user.Username, role, Hash(user.Username), [Hash(user.Password)]));
        }

        _accounts = [.. accounts];
    }

    /// <summary>Returns the authenticated principal, or null if the pair matches no account.</summary>
    public ClaimsPrincipal? Validate(string username, string password)
    {
        var usernameHash = Hash(username);
        var passwordHash = Hash(password);

        Account? match = null;
        foreach (var account in _accounts)
        {
            var usernameMatches = CryptographicOperations.FixedTimeEquals(usernameHash, account.UsernameHash);
            var passwordMatches = false;
            foreach (var candidate in account.PasswordHashes)
            {
                passwordMatches |= CryptographicOperations.FixedTimeEquals(passwordHash, candidate);
            }

            if (usernameMatches & passwordMatches)
            {
                match = account;
            }
        }

        if (match is null)
        {
            return null;
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, match.Username),
                new Claim(ClaimTypes.Name, match.Username),
                new Claim(ClaimTypes.Role, match.Role),
            ],
            BasicAuthenticationHandler.SchemeName);
        return new ClaimsPrincipal(identity);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
