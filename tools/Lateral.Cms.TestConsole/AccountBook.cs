using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.TestConsole;

/// <summary>
/// The CMS accounts the console can act as. Credentials stay on the server: the browser only ever names an
/// account ("user", "admin", ...) and the proxy adds the Authorization header.
/// </summary>
public sealed class AccountBook
{
    public const string Cms = "cms";
    public const string User = "user";
    public const string Admin = "admin";

    private readonly Dictionary<string, (string Username, string Password)> _accounts =
        new(StringComparer.OrdinalIgnoreCase);

    public AccountBook(IOptions<ConsoleOptions> options, IHostEnvironment environment)
    {
        var settings = options.Value;

        if (environment.IsDevelopment() && !string.IsNullOrWhiteSpace(settings.CmsDevSettingsPath))
        {
            var path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, settings.CmsDevSettingsPath));
            if (File.Exists(path))
            {
                foreach (var (id, account) in ReadCmsSettings(File.ReadAllText(path)))
                {
                    _accounts[id] = account;
                }
            }
        }

        foreach (var (id, account) in settings.Accounts)
        {
            if (account.Username.Length > 0 && account.Password.Length > 0)
            {
                _accounts[id] = (account.Username, account.Password);
            }
        }
    }

    public IEnumerable<string> Configured => _accounts.Keys;

    public bool Has(string id) => _accounts.ContainsKey(id);

    /// <summary>The value for an Authorization header, or null if the account is not configured.</summary>
    public string? BasicHeader(string id, string? passwordOverride = null) =>
        _accounts.TryGetValue(id, out var account) ? Basic(account.Username, passwordOverride ?? account.Password) : null;

    /// <summary>A username that exists, for the "right user, wrong password" case.</summary>
    public string? AnyUsername() =>
        _accounts.TryGetValue(User, out var user) ? user.Username
        : _accounts.TryGetValue(Cms, out var cms) ? cms.Username
        : _accounts.Values.Select(a => a.Username).FirstOrDefault();

    public static string Basic(string username, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    /// <summary>Reads the accounts out of a CMS settings file (its <c>Auth</c> section).</summary>
    public static Dictionary<string, (string Username, string Password)> ReadCmsSettings(string json)
    {
        var found = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("Auth", out var auth))
        {
            return found;
        }

        if (auth.TryGetProperty("Cms", out var cms)
            && cms.TryGetProperty("Username", out var cmsUser) && cmsUser.ValueKind == JsonValueKind.String
            && cms.TryGetProperty("Passwords", out var passwords) && passwords.ValueKind == JsonValueKind.Array
            && passwords.GetArrayLength() > 0 && passwords[0].ValueKind == JsonValueKind.String)
        {
            found[Cms] = (cmsUser.GetString()!, passwords[0].GetString()!);
        }

        if (auth.TryGetProperty("Users", out var users) && users.ValueKind == JsonValueKind.Array)
        {
            foreach (var user in users.EnumerateArray())
            {
                if (user.TryGetProperty("Username", out var name) && name.ValueKind == JsonValueKind.String
                    && user.TryGetProperty("Password", out var password) && password.ValueKind == JsonValueKind.String)
                {
                    var role = user.TryGetProperty("Role", out var r) ? r.GetString() : User;
                    var id = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ? Admin : User;
                    found.TryAdd(id, (name.GetString()!, password.GetString()!));
                }
            }
        }

        return found;
    }
}
