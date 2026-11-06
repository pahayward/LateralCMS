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

        if (options.Users.Count == 0)
        {
            errors.Add("Auth:Users needs at least one user.");
        }

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

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
