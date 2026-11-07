using Microsoft.Extensions.Options;

namespace Lateral.Cms.Auth;

public static class Roles
{
    public const string CmsIngest = "CmsIngest";
    public const string User = "User";
    public const string Admin = "Admin";
}

public static class Policies
{
    public const string CmsIngest = "CmsIngest";
    public const string ApiRead = "ApiRead";
    public const string AdminOnly = "AdminOnly";

    /// <summary>The CMS follows its own batches; an admin investigates them. Users have no business here.</summary>
    public const string BatchStatus = "BatchStatus";
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The organisation account: the CMS pushing events. May call /cms/events and nothing else.</summary>
    public CmsAccountOptions Cms { get; set; } = new();

    /// <summary>People reading the API. Role is <c>User</c> or <c>Admin</c>.</summary>
    public List<UserAccountOptions> Users { get; set; } = [];

    /// <summary>Failed sign-ins per client address per minute before further attempts are throttled. 0 disables.</summary>
    public int MaxFailuresPerMinute { get; set; } = 10;

    /// <summary>
    /// PBKDF2 iterations for new password hashes. 600000 is the current OWASP guidance for PBKDF2-HMAC-SHA256 and costs about
    /// a tenth of a second per sign-in. Lower values exist only so tests are quick; startup warns about anything under 100000.
    /// </summary>
    public int PasswordHashIterations { get; set; } = PasswordHasher.DefaultIterations;

    /// <summary>How long a browser session lasts without activity. Each request renews it.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>
    /// How long a person's record is trusted before it is read again. A change made through the API takes effect at once; a change
    /// made directly in the database (the command-line tool) is noticed within this time. 0 reads it every request.
    /// </summary>
    public int UserCacheSeconds { get; set; } = 5;

    /// <summary>
    /// How long a correct username and password sent as Basic credentials is remembered, so scripts do not pay for a slow hash on
    /// every request. A password change or disabling the account ends it at once. 0 turns it off.
    /// </summary>
    public int VerifiedCredentialCacheMinutes { get; set; } = 5;

    /// <summary>Folder for the keys that sign session cookies, so sessions survive a restart. Defaults to <c>keys</c> beside the database.</summary>
    public string? KeysPath { get; set; }

    /// <summary>Username of the administrator created on first run when no accounts exist.</summary>
    public string BootstrapAdminUsername { get; set; } = "admin";
}

public sealed class CmsAccountOptions
{
    /// <summary>10-20 characters.</summary>
    public string Username { get; set; } = "";

    /// <summary>One or more GUIDs. Several may be active at once so the credential can be rotated without downtime.</summary>
    public List<string> Passwords { get; set; } = [];
}

public sealed class UserAccountOptions
{
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string Role { get; set; } = Roles.User;
}

/// <summary>Fails startup on missing or malformed credentials, rather than serving with weak defaults.</summary>
public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public const int MinUserPasswordLength = 12;

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var errors = new List<string>();

        var cmsUser = options.Cms.Username;
        if (cmsUser is null || cmsUser.Length is < 10 or > 20)
        {
            errors.Add("Auth:Cms:Username must be 10 to 20 characters.");
        }
        else if (cmsUser.Contains(':'))
        {
            errors.Add("Auth:Cms:Username must not contain ':'.");
        }

        if (options.Cms.Passwords.Count == 0)
        {
            errors.Add("Auth:Cms:Passwords needs at least one password (a GUID).");
        }

        for (var i = 0; i < options.Cms.Passwords.Count; i++)
        {
            if (!Guid.TryParse(options.Cms.Passwords[i], out var guid) || guid == Guid.Empty)
            {
                errors.Add($"Auth:Cms:Passwords:{i} must be a non-empty GUID.");
            }
        }

        // Auth:Users may be empty: people live in the database, and the first administrator is created on first run.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(cmsUser))
        {
            seen.Add(cmsUser);
        }

        for (var i = 0; i < options.Users.Count; i++)
        {
            var user = options.Users[i];
            if (string.IsNullOrWhiteSpace(user.Username) || user.Username.Contains(':'))
            {
                errors.Add($"Auth:Users:{i}:Username must be non-empty and must not contain ':'.");
            }
            else if (!seen.Add(user.Username))
            {
                errors.Add($"Auth:Users:{i}:Username '{user.Username}' is not unique (the CMS account counts too).");
            }

            if (user.Password is null || user.Password.Length < MinUserPasswordLength)
            {
                errors.Add($"Auth:Users:{i}:Password must be at least {MinUserPasswordLength} characters.");
            }

            if (!string.Equals(user.Role, Roles.User, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(user.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Auth:Users:{i}:Role must be '{Roles.User}' or '{Roles.Admin}'.");
            }
        }

        if (options.MaxFailuresPerMinute < 0)
        {
            errors.Add("Auth:MaxFailuresPerMinute must not be negative.");
        }

        if (options.PasswordHashIterations is < 1_000 or > 10_000_000)
        {
            errors.Add("Auth:PasswordHashIterations must be between 1000 and 10000000.");
        }

        if (options.SessionHours is < 1 or > 24 * 30)
        {
            errors.Add("Auth:SessionHours must be between 1 and 720.");
        }

        if (options.UserCacheSeconds is < 0 or > 300)
        {
            errors.Add("Auth:UserCacheSeconds must be between 0 and 300.");
        }

        if (options.VerifiedCredentialCacheMinutes is < 0 or > 60)
        {
            errors.Add("Auth:VerifiedCredentialCacheMinutes must be between 0 and 60.");
        }

        var bootstrap = options.BootstrapAdminUsername;
        if (string.IsNullOrWhiteSpace(bootstrap) || bootstrap.Length > 100 || bootstrap.Contains(':'))
        {
            errors.Add("Auth:BootstrapAdminUsername must be 1 to 100 characters and must not contain ':'.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
