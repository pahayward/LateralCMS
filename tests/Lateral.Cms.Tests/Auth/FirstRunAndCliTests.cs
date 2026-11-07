using System.Net;
using Lateral.Cms.Admin;
using Lateral.Cms.Auth;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Auth;

/// <summary>A fresh install has no accounts at all. It makes the first administrator itself, with a password nobody chose.</summary>
public class FirstRunTests : IClassFixture<FirstRunTests.FreshInstall>
{
    public sealed class FreshInstall : CmsFactory
    {
        private static readonly string Folder = Path.Combine(Path.GetTempPath(), "lateral-firstrun-" + Guid.NewGuid().ToString("N"));

        public FreshInstall()
            : base(Path.Combine(Directory.CreateDirectory(Folder).FullName, "cms.db"), ownsDatabase: true)
        {
        }

        protected override bool ConfiguredUsers => false;

        public string PasswordFile => Path.Combine(Folder, InitialPasswordFile.FileName);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try
                {
                    Directory.Delete(Folder, recursive: true);
                }
                catch (IOException)
                {
                    // Best effort: it is a temp folder.
                }
            }
        }
    }

    private readonly FreshInstall _factory;

    public FirstRunTests(FreshInstall factory) => _factory = factory;

    [Fact]
    [Trait("req", "AC-06")]
    public async Task The_first_administrator_is_made_with_a_temporary_password_that_is_in_a_file_and_not_in_the_log_and_goes_when_it_is_replaced()
    {
        _ = _factory.Services;

        var rows = _factory.Sql("SELECT Username, Role, MustChangePassword FROM Users");
        Assert.Single(rows);
        Assert.Equal("admin", (string)rows[0][0]!);
        Assert.Equal("Admin", (string)rows[0][1]!);
        Assert.Equal(1L, (long)rows[0][2]!);

        Assert.True(File.Exists(_factory.PasswordFile));
        var lines = File.ReadAllLines(_factory.PasswordFile);
        var temporary = lines.Single(l => l.StartsWith("password: ", StringComparison.Ordinal))["password: ".Length..];

        var everything = string.Join('\n', _factory.Logs.Entries.Select(e => e.Message + e.Exception));
        Assert.DoesNotContain(temporary, everything, StringComparison.Ordinal);
        Assert.Contains("initial-admin-password.txt", everything, StringComparison.Ordinal);

        // Until it is replaced, it can do nothing else.
        var admin = _factory.ClientFor("admin", temporary);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/users")).StatusCode);

        var replacement = AccountHelpers.NewPassword();
        var changed = await admin.PostAsync("/api/auth/password", AccountHelpers.Json(new { current = temporary, newPassword = replacement }));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.False(File.Exists(_factory.PasswordFile));
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientFor("admin", replacement).GetAsync("/api/admin/users")).StatusCode);
        Assert.DoesNotContain(replacement, string.Join('\n', _factory.Logs.Entries.Select(e => e.Message + e.Exception)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("req", "AC-06")]
    public async Task Nothing_is_made_when_somebody_can_already_sign_in()
    {
        _ = _factory.Services;
        var accounts = _factory.Services.GetRequiredService<AccountService>();

        Assert.Null(await accounts.BootstrapAsync(CancellationToken.None));
        Assert.Equal(1, _factory.Count("SELECT COUNT(*) FROM Users"));
    }

    [Fact]
    [Trait("req", "AC-06")]
    public void A_configuration_with_no_people_in_it_is_valid()
    {
        var options = new AuthOptions { Cms = new CmsAccountOptions { Username = "cms-ingest-svc", Passwords = [Guid.NewGuid().ToString()] } };

        Assert.Equal(Microsoft.Extensions.Options.ValidateOptionsResult.Success, new AuthOptionsValidator().Validate(null, options));
    }
}

/// <summary>The operator's command-line tool, run on the machine (over SSH), against the same database the service uses.</summary>
public class AdminCliTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private sealed record Ran(int Code, string Out, string Error);

    private async Task<Ran> RunAsync(string input, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await AdminCli.RunAsync(factory.Services, ["admin", .. args], output, error, new StringReader(input));
        return new Ran(code, output.ToString(), error.ToString());
    }

    [Fact]
    [Trait("req", "AC-07")]
    public async Task Help_lists_the_commands_and_an_unknown_command_is_refused()
    {
        var help = await RunAsync("", "help");
        Assert.Equal(0, help.Code);
        foreach (var command in new[] { "list-users", "create-user", "set-password", "set-role", "disable-user", "enable-user", "revoke-sessions", "delete-user", "new-service-secret", "where" })
        {
            Assert.Contains(command, help.Out, StringComparison.Ordinal);
        }

        var unknown = await RunAsync("", "frobnicate");
        Assert.Equal(2, unknown.Code);
        Assert.Contains("Unknown command", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("req", "AC-07")]
    public async Task A_password_can_be_set_from_standard_input_and_is_never_printed_back()
    {
        var (name, old) = await AccountHelpers.MakePersonAsync(factory);
        var chosen = AccountHelpers.NewPassword();

        var ran = await RunAsync(chosen + "\n", "set-password", name, "--stdin");

        Assert.Equal(0, ran.Code);
        Assert.DoesNotContain(chosen, ran.Out + ran.Error, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(name, old).GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, chosen).GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-07")]
    public async Task A_typed_password_is_asked_for_twice_and_nothing_changes_if_the_two_differ()
    {
        var (name, old) = await AccountHelpers.MakePersonAsync(factory);
        var typed = AccountHelpers.NewPassword();

        var mismatch = await RunAsync($"{typed}\n{typed}x\n", "set-password", name);
        Assert.Equal(1, mismatch.Code);
        Assert.Contains("differ", mismatch.Error, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, old).GetAsync("/api/entities")).StatusCode);

        var matched = await RunAsync($"{typed}\n{typed}\n", "set-password", name);
        Assert.Equal(0, matched.Code);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, typed).GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-07")]
    public async Task A_weak_password_is_refused_and_a_missing_person_is_reported()
    {
        var (name, old) = await AccountHelpers.MakePersonAsync(factory);

        var weak = await RunAsync("short\n", "set-password", name, "--stdin");
        Assert.Equal(1, weak.Code);
        Assert.Contains("at least", weak.Error, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, old).GetAsync("/api/entities")).StatusCode);

        var missing = await RunAsync(AccountHelpers.NewPassword() + "\n", "set-password", "nobody@x.example", "--stdin");
        Assert.Equal(1, missing.Code);
        Assert.Contains("No such user", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("req", "AC-07")]
    public async Task A_generated_password_is_printed_once_and_must_be_replaced_at_the_next_sign_in()
    {
        var (name, _) = await AccountHelpers.MakePersonAsync(factory);

        var ran = await RunAsync("", "set-password", name, "--generate");

        Assert.Equal(0, ran.Code);
        var temporary = ran.Out.Split('\n').Single(l => l.StartsWith("Temporary password: ", StringComparison.Ordinal))["Temporary password: ".Length..].Trim();
        var script = factory.ClientFor(name, temporary);
        Assert.Equal("password_change_required", await AccountHelpers.CodeAsync(await script.GetAsync("/api/entities")));
    }

    [Fact]
    [Trait("req", "AC-07")]
    public async Task A_person_can_be_created_listed_promoted_disabled_signed_out_and_deleted_from_the_command_line()
    {
        await AccountHelpers.MakePersonAsync(factory, "Admin");
        var name = AccountHelpers.NewName();
        var password = AccountHelpers.NewPassword();

        var created = await RunAsync(password + "\n", "create-user", name, "--role", "admin", "--stdin");
        Assert.Equal(0, created.Code);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, password).GetAsync("/api/admin/users")).StatusCode);

        var list = await RunAsync("", "list-users");
        Assert.Contains(name, list.Out, StringComparison.Ordinal);
        Assert.Contains(CmsFactory.AdminName, list.Out, StringComparison.Ordinal);
        Assert.Contains("configuration (read-only)", list.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(password, list.Out, StringComparison.Ordinal);

        Assert.Equal(0, (await RunAsync("", "set-role", name, "user")).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(name, password).GetAsync("/api/admin/users")).StatusCode);

        var browser = await AccountHelpers.SignedInAsync(factory, name, password);
        Assert.Equal(0, (await RunAsync("", "revoke-sessions", name)).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/entities")).StatusCode);

        Assert.Equal(0, (await RunAsync("", "disable-user", name)).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(name, password).GetAsync("/api/entities")).StatusCode);
        Assert.Contains("disabled", (await RunAsync("", "list-users")).Out, StringComparison.Ordinal);
        Assert.Equal(0, (await RunAsync("", "enable-user", name)).Code);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, password).GetAsync("/api/entities")).StatusCode);

        Assert.Equal(0, (await RunAsync("", "delete-user", name)).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(name, password).GetAsync("/api/entities")).StatusCode);

        var actors = factory.Sql($"SELECT DISTINCT Actor FROM AuditLog WHERE Target = '{name}' AND Action LIKE 'user.%'").Select(r => (string)r[0]!);
        Assert.All(actors, a => Assert.StartsWith("cli:", a, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("req", "AC-07")]
    public async Task A_generated_service_secret_is_a_guid_and_where_names_the_real_locations()
    {
        var secret = await RunAsync("", "new-service-secret");
        Assert.True(Guid.TryParse(secret.Out.Trim(), out var guid) && guid != Guid.Empty);

        var where = await RunAsync("", "where");
        Assert.Equal(0, where.Code);
        Assert.Contains(factory.DatabasePath, where.Out, StringComparison.Ordinal);
        Assert.Contains(factory.AssetsPath, where.Out, StringComparison.Ordinal);
        Assert.Contains(factory.KeysPath, where.Out, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("req", "AC-07")]
    public void Help_and_a_new_service_secret_need_neither_configuration_nor_a_database_and_everything_else_does()
    {
        using var output = new StringWriter();

        Assert.Equal(0, AdminCli.RunWithoutService(["admin", "help"], output));
        Assert.Equal(0, AdminCli.RunWithoutService(["admin"], output));
        Assert.Equal(0, AdminCli.RunWithoutService(["admin", "new-service-secret"], output));
        Assert.Null(AdminCli.RunWithoutService(["admin", "list-users"], output));
        Assert.Null(AdminCli.RunWithoutService(["admin", "set-password", "someone"], output));
    }

    [Fact]
    [Trait("req", "AC-07")]
    public void Operator_words_are_recognised_only_as_the_first_argument()
    {
        Assert.True(AdminCli.IsAdminCommand(["admin", "list-users"]));
        Assert.True(AdminCli.IsAdminCommand(["ADMIN"]));
        Assert.False(AdminCli.IsAdminCommand(["--urls", "http://x", "admin"]));
        Assert.False(AdminCli.IsAdminCommand([]));
    }
}

/// <summary>Nobody can lock the installation out through the tool either: the last administrator stays.</summary>
public class AdminCliGuardTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    [Fact]
    [Trait("req", "AC-07")]
    public async Task The_tool_will_not_remove_the_last_administrator_but_will_reset_their_password()
    {
        var (only, _) = await AccountHelpers.MakePersonAsync(factory, "Admin");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = await AdminCli.RunAsync(factory.Services, ["admin", "delete-user", only], output, error, new StringReader(""));
        Assert.Equal(1, code);
        Assert.Contains("only enabled administrator", error.ToString(), StringComparison.Ordinal);

        var reset = await AdminCli.RunAsync(factory.Services, ["admin", "set-password", only, "--generate"], output, error, new StringReader(""));
        Assert.Equal(0, reset);
    }
}
