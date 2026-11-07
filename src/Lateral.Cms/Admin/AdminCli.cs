using System.Text;
using Lateral.Cms.Assets;
using Lateral.Cms.Auth;
using Lateral.Cms.Persistence;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Admin;

/// <summary>
/// The operator's tool, run on the machine itself (over SSH on a server): <c>dotnet Lateral.Cms.dll admin &lt;command&gt;</c>. It opens the
/// same database the service uses, with the same configuration, and changes accounts directly, so it works when nobody can sign in
/// and when the service is not running. Being able to run it is the authority: whoever can read and write the database file could
/// do the same by hand. Every change it makes is written to the audit log as <c>cli:&lt;operating-system user&gt;</c>.
/// </summary>
public static class AdminCli
{
    public const string Verb = "admin";

    public static bool IsAdminCommand(string[] args) => args.Length > 0 && args[0].Equals(Verb, StringComparison.OrdinalIgnoreCase);

    public const string Help = """
        Lateral CMS operator commands
          dotnet Lateral.Cms.dll admin <command> [arguments]

        People
          list-users                                   Show who can sign in, and who is disabled
          create-user <name> [--role user|admin]       Add a person. Asks for a password, or --generate makes one
          set-password <name>                          Choose a new password for someone (asks twice, nothing is shown)
          set-password <name> --generate               Make a temporary password and print it once; they must replace it
          set-password <name> --stdin                  Read the password from standard input (for scripts)
          set-role <name> <user|admin>
          disable-user <name> | enable-user <name>
          revoke-sessions <name>                       Sign them out everywhere, keeping the password
          delete-user <name>

        Other
          new-service-secret                           Print a new GUID for the CMS service account (Auth:Cms:Passwords)
          where                                        Show where this installation keeps its database, assets and keys
          help

        A person's password is never printed back, except one this tool generated. The last enabled administrator cannot be
        disabled, demoted or deleted; if every administrator is locked out, set-password on one of them or create-user a new one.
        """;

    /// <summary>
    /// The commands that need neither the database nor valid configuration: help, and making a service secret, which is what you do
    /// before the configuration exists. Returns null for any other command, which needs the service's own setup.
    /// </summary>
    public static int? RunWithoutService(string[] args, TextWriter output)
    {
        switch (args.Length > 1 ? args[1].ToLowerInvariant() : "help")
        {
            case "help" or "--help" or "-h":
                output.WriteLine(Help);
                return 0;

            case "new-service-secret":
                output.WriteLine(Guid.NewGuid());
                return 0;

            default:
                return null;
        }
    }

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, TextWriter error, TextReader input)
    {
        var command = args.Length > 1 ? args[1].ToLowerInvariant() : "help";
        var rest = args.Skip(2).ToArray();
        var accounts = services.GetRequiredService<AccountService>();
        var actor = $"cli:{Environment.UserName}";

        try
        {
            switch (command)
            {
                case "help" or "--help" or "-h":
                    output.WriteLine(Help);
                    return 0;

                case "new-service-secret":
                    output.WriteLine(Guid.NewGuid());
                    return 0;

                case "where":
                    return Where(services, output);

                case "list-users":
                    return await ListAsync(services, accounts, output);

                case "create-user":
                {
                    var (name, flags) = Split(rest);
                    if (name is null)
                    {
                        return Usage(error, "create-user <name> [--role user|admin] [--generate | --stdin]");
                    }

                    var role = flags.TryGetValue("role", out var r) ? r : Roles.User;
                    var password = ChoosePassword(flags, name, output, error, input);
                    if (password.Failed)
                    {
                        return 1;
                    }

                    var change = await accounts.CreateAsync(actor, name, role ?? Roles.User, password.Value, CancellationToken.None);
                    return Report(change, $"Created {name} ({change.User?.Role}).", output, error);
                }

                case "set-password":
                {
                    var (name, flags) = Split(rest);
                    if (name is null)
                    {
                        return Usage(error, "set-password <name> [--generate | --stdin]");
                    }

                    var password = ChoosePassword(flags, name, output, error, input);
                    if (password.Failed)
                    {
                        return 1;
                    }

                    var change = await accounts.ResetPasswordAsync(actor, name, password.Value, flags.ContainsKey("require-change") ? true : null, CancellationToken.None);
                    if (change.Ok)
                    {
                        services.GetRequiredService<InitialPasswordFile>().DeleteIfFor(change.User!.Username);
                    }

                    return Report(change, $"The password for {name} was changed and every session they had has ended.", output, error);
                }

                case "set-role":
                    return rest.Length == 2
                        ? Report(await accounts.SetRoleAsync(actor, rest[0], rest[1], CancellationToken.None), $"{rest[0]} is now {rest[1]}.", output, error)
                        : Usage(error, "set-role <name> <user|admin>");

                case "disable-user" or "enable-user":
                    return rest.Length == 1
                        ? Report(await accounts.SetDisabledAsync(actor, rest[0], command == "disable-user", CancellationToken.None), $"{rest[0]} is now {(command == "disable-user" ? "disabled" : "enabled")}.", output, error)
                        : Usage(error, $"{command} <name>");

                case "revoke-sessions":
                    return rest.Length == 1
                        ? Report(await accounts.RevokeSessionsAsync(actor, rest[0], CancellationToken.None), $"{rest[0]} is signed out everywhere.", output, error)
                        : Usage(error, "revoke-sessions <name>");

                case "delete-user":
                    return rest.Length == 1
                        ? Report(await accounts.DeleteAsync(actor, rest[0], CancellationToken.None), $"{rest[0]} was deleted.", output, error)
                        : Usage(error, "delete-user <name>");

                default:
                    error.WriteLine($"Unknown command '{command}'.");
                    error.WriteLine();
                    error.WriteLine(Help);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or Microsoft.EntityFrameworkCore.DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            error.WriteLine($"The database could not be used: {ex.Message}");
            return 1;
        }
    }

    private static int Where(IServiceProvider services, TextWriter output)
    {
        var database = services.GetRequiredService<DatabaseConnections>();
        var assets = services.GetRequiredService<IOptions<AssetOptions>>().Value;
        var auth = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var environment = services.GetRequiredService<IHostEnvironment>();
        var root = environment.ContentRootPath;
        output.WriteLine($"Environment      {environment.EnvironmentName}");
        output.WriteLine($"Database         {database.DataSource}");
        output.WriteLine($"Asset store      {(Path.IsPathRooted(assets.Root) ? assets.Root : Path.GetFullPath(Path.Combine(root, assets.Root)))}");
        output.WriteLine($"Session keys     {(auth.KeysPath is { Length: > 0 } k ? Path.GetFullPath(k) : Path.Combine(Path.GetDirectoryName(database.DataSource)!, "keys"))}");
        output.WriteLine($"Configuration    {root} (appsettings.json, appsettings.{environment.EnvironmentName}.json) and environment variables");
        return 0;
    }

    private static async Task<int> ListAsync(IServiceProvider services, AccountService accounts, TextWriter output)
    {
        var store = services.GetRequiredService<CredentialStore>();
        output.WriteLine($"{"NAME",-34}{"ROLE",-10}{"STATE",-26}LAST SIGN-IN (UTC)");
        foreach (var (name, role) in store.ConfiguredAccounts)
        {
            output.WriteLine($"{name,-34}{role,-10}{"configuration (read-only)",-26}-");
        }

        var people = await accounts.ListAsync(CancellationToken.None);
        foreach (var person in people)
        {
            var state = person.Disabled ? "disabled" : person.MustChangePassword ? "must change password" : "active";
            output.WriteLine($"{person.Username,-34}{person.Role,-10}{state,-26}{(person.LastSignInAt is { } at ? at.ToString("u") : "never")}");
        }

        if (people.Count == 0)
        {
            output.WriteLine();
            output.WriteLine("Nobody is in the database yet. Start the service and it makes the first administrator, or run: create-user <name> --role admin");
        }

        return 0;
    }

    private sealed record Chosen(string? Value, bool Failed);

    /// <summary>The password for create-user and set-password: generated, read from stdin, or asked for twice without echo.</summary>
    private static Chosen ChoosePassword(Dictionary<string, string?> flags, string name, TextWriter output, TextWriter error, TextReader input)
    {
        if (flags.ContainsKey("generate"))
        {
            // Null makes the service generate one and report it as the temporary password.
            return new Chosen(null, false);
        }

        string? first;
        if (flags.ContainsKey("stdin"))
        {
            first = input.ReadLine();
        }
        else
        {
            first = ReadHidden($"New password for {name}: ", output, input);
            var again = ReadHidden("Again: ", output, input);
            if (first != again)
            {
                error.WriteLine("The two passwords differ. Nothing was changed.");
                return new Chosen(null, true);
            }
        }

        if (string.IsNullOrEmpty(first))
        {
            error.WriteLine("No password was given. Nothing was changed.");
            return new Chosen(null, true);
        }

        return new Chosen(first, false);
    }

    private static string ReadHidden(string prompt, TextWriter output, TextReader input)
    {
        output.Write(prompt);
        if (Console.IsInputRedirected)
        {
            var line = input.ReadLine() ?? "";
            output.WriteLine();
            return line;
        }

        var text = new StringBuilder();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        output.WriteLine();
        return text.ToString();
    }

    private static (string? Name, Dictionary<string, string?> Flags) Split(string[] args)
    {
        string? name = null;
        var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i][2..];
                flags[key] = key == "role" && i + 1 < args.Length ? args[++i] : null;
            }
            else
            {
                name ??= args[i];
            }
        }

        return (name, flags);
    }

    private static int Report(AccountChange change, string success, TextWriter output, TextWriter error)
    {
        if (!change.Ok)
        {
            error.WriteLine(change.Message);
            return 1;
        }

        output.WriteLine(success);
        if (change.TemporaryPassword is { } temporary)
        {
            output.WriteLine();
            output.WriteLine($"Temporary password: {temporary}");
            output.WriteLine("It is shown once and must be replaced at the next sign-in.");
        }

        return 0;
    }

    private static int Usage(TextWriter error, string usage)
    {
        error.WriteLine($"Usage: dotnet Lateral.Cms.dll admin {usage}");
        return 2;
    }
}
