using System.ComponentModel.DataAnnotations;

namespace Lateral.Cms.TestConsole;

public sealed class ConsoleOptions
{
    public const string SectionName = "Console";

    /// <summary>Address of the CMS service under test.</summary>
    [Required, Url]
    public string CmsBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Loopback port the console listens on. It never listens on any other address.</summary>
    [Range(1024, 65535)]
    public int Port { get; set; } = 5100;

    /// <summary>
    /// In Development only: the CMS's own development settings file, read for its test accounts so the
    /// credentials live in one place. Ignored if the file does not exist.
    /// </summary>
    public string? CmsDevSettingsPath { get; set; } = "../../src/Lateral.Cms/App_Data/dev-credentials.json";

    /// <summary>Explicit accounts, keyed <c>cms</c>, <c>user</c> or <c>admin</c>. They win over the CMS settings file.</summary>
    /// <summary>
    /// The console holds live credentials and can act as an admin, so it refuses to start outside the Development environment. Set
    /// this to run it anyway (for example against a staging service), knowingly.
    /// </summary>
    public bool AllowNonDevelopment { get; set; }

    /// <summary>The sign-in in front of every page and call. On by default; the demo people are admin/admin and user/password.</summary>
    public SignInOptions SignIn { get; set; } = new();

    public Dictionary<string, AccountOptions> Accounts { get; set; } = new();

    public LimitsOptions Limits { get; set; } = new();
}

public sealed class AccountOptions
{
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";
}

/// <summary>
/// What the UI tells the user the CMS accepts. These mirror the CMS defaults for display only; the probes show
/// what the server really does.
/// </summary>
public sealed class LimitsOptions
{
    public long MaxBodyBytes { get; set; } = 5 * 1024 * 1024;

    public long MaxPayloadBytes { get; set; } = 256 * 1024;

    public int MaxBatchSize { get; set; } = 500;

    /// <summary>Largest object the CMS takes in one PUT; bigger ones are uploaded in parts. Mirrors the CMS default, for the page's choice only.</summary>
    public long MaxSimpleBytes { get; set; } = 8L * 1024 * 1024;

    public long MinPartBytes { get; set; } = 5L * 1024 * 1024;

    public long MaxPartBytes { get; set; } = 64L * 1024 * 1024;
}

/// <summary>Who may open the console. Demo defaults, set in configuration (<c>Console:SignIn:People</c>) for anything else.</summary>
public sealed class SignInOptions
{
    public bool Enabled { get; set; } = true;

    public List<PersonOptions> People { get; set; } =
    [
        new() { Name = "admin", Password = "admin", Role = SignIn.AdminRole },
        new() { Name = "user", Password = "password", Role = SignIn.UserRole },
    ];
}

public sealed class PersonOptions
{
    public string Name { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary><c>admin</c> (everything) or <c>user</c> (the photo library only).</summary>
    public string Role { get; set; } = SignIn.UserRole;
}
