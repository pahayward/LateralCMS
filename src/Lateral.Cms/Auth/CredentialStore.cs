using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Auth;

/// <summary>Claim types and header names the authentication code shares.</summary>
public static class AccountClaims
{
    /// <summary>The person's database id; absent for accounts defined in configuration.</summary>
    public const string UserId = "lateral:uid";

    /// <summary>Changes whenever the password is changed or the account disabled; a session made before that is refused.</summary>
    public const string Stamp = "lateral:stamp";

    /// <summary>Present, and <c>true</c>, while the person must choose a new password before doing anything else.</summary>
    public const string MustChange = "lateral:must-change";

    /// <summary><c>config</c> or <c>database</c>.</summary>
    public const string Source = "lateral:source";

    /// <summary>
    /// A page that has its own sign-in form sends this header. The service then answers a missing sign-in with a plain 401 and no
    /// <c>WWW-Authenticate</c>, so the browser does not put its own password box over the page's form. Any value will do.
    /// </summary>
    public const string ClientHeader = "X-Requested-With";

    public static ClaimsPrincipal Principal(UserSnapshot person, string scheme)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, person.Username),
            new(ClaimTypes.Name, person.Username),
            new(ClaimTypes.Role, person.Role),
            new(UserId, person.Id.ToString()),
            new(Stamp, person.Stamp.ToString()),
            new(Source, "database"),
        };
        if (person.MustChangePassword)
        {
            claims.Add(new Claim(MustChange, "true"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }
}

/// <summary>
/// Checks Basic-auth credentials against the configured accounts and the people in the database.
/// </summary>
/// <remarks>
/// Configured accounts (the CMS service account and any <c>Auth:Users</c>) are compared in constant time: values are hashed first
/// (so length differences don't leak) and compared with <see cref="CryptographicOperations.FixedTimeEquals"/>, and every account is
/// always checked so the response time does not reveal which usernames exist. People in the database are checked with a slow
/// salted hash (see <see cref="AccountService"/>). The CMS account's secrets live in configuration, where a deployment keeps them in
/// a secret store or environment variables (see the README).
/// </remarks>
public sealed class CredentialStore
{
    private sealed record Account(string Username, string Role, byte[] UsernameHash, byte[][] PasswordHashes);

    private readonly Account[] _accounts;
    private readonly AccountService? _people;

    public CredentialStore(IOptions<AuthOptions> options, AccountService? people = null)
    {
        _people = people;
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

    /// <summary>The accounts defined in configuration, which nobody can change or delete through the service.</summary>
    public IEnumerable<(string Username, string Role)> ConfiguredAccounts => _accounts.Select(a => (a.Username, a.Role));

    /// <summary>
    /// The principal for a pair that matches a configured account or a person in the database, or null. A failure costs one slow
    /// password hash (against a decoy when the person does not exist), so timing does not say which usernames are real.
    /// </summary>
    public async Task<ClaimsPrincipal?> ValidateAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (Validate(username, password) is { } configured)
        {
            return configured;
        }

        if (_people is null)
        {
            return null;
        }

        return await _people.AuthenticateAsync(username, password, cancellationToken) is { } person
            ? AccountClaims.Principal(person, BasicAuthenticationHandler.SchemeName)
            : null;
    }

    /// <summary>Checks the configured accounts only.</summary>
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
                new Claim(AccountClaims.Source, "config"),
            ],
            BasicAuthenticationHandler.SchemeName);
        return new ClaimsPrincipal(identity);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
