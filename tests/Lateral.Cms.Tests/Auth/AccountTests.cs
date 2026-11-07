using System.Net;
using System.Text;
using System.Text.Json;
using Lateral.Cms.Auth;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Auth;

/// <summary>Shared by the account tests: people made through the API, signing in both ways, and reading what comes back.</summary>
public static class AccountHelpers
{
    public static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public static string NewName() => $"t-{Guid.NewGuid():N}"[..14] + "@x.example";

    public static string NewPassword() => "pw-" + Guid.NewGuid().ToString("N");

    /// <summary>Makes a person as the configured administrator and returns their name and password.</summary>
    public static async Task<(string Name, string Password)> MakePersonAsync(CmsFactory factory, string role = "User", string? password = null)
    {
        var name = NewName();
        password ??= NewPassword();
        var made = await factory.Admin().PostAsync("/api/admin/users", Json(new { username = name, role, password }));
        Assert.True(made.StatusCode == HttpStatusCode.Created, await made.Content.ReadAsStringAsync());
        return (name, password);
    }

    /// <summary>A browser: its own cookie jar, signed in through the login form.</summary>
    public static async Task<HttpClient> SignedInAsync(CmsFactory factory, string name, string password)
    {
        var browser = factory.CreateClient();
        var response = await browser.PostAsync("/api/auth/login", Json(new { username = name, password }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return browser;
    }

    public static HttpRequestMessage FromPage(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(AccountClaims.ClientHeader, "page");
        return request;
    }

    public static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        using var body = await response.ReadJsonAsync();
        return body.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var body = await response.ReadJsonAsync();
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}

/// <summary>Passwords are stored as slow salted hashes, and a good password is told from a poor one.</summary>
public class PasswordTests
{
    [Fact]
    [Trait("req", "AC-01")]
    public void A_hash_checks_the_right_password_refuses_others_and_never_contains_the_password()
    {
        var stored = PasswordHasher.Hash("correct horse battery", 1000);

        Assert.True(PasswordHasher.Verify("correct horse battery", stored, 1000, out var stale));
        Assert.False(stale);
        Assert.False(PasswordHasher.Verify("correct horse batterz", stored, 1000, out _));
        Assert.DoesNotContain("correct", stored, StringComparison.Ordinal);
        Assert.StartsWith("pbkdf2-sha256$1000$", stored, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("req", "AC-01")]
    public void Two_hashes_of_the_same_password_differ_because_each_has_its_own_salt()
    {
        Assert.NotEqual(PasswordHasher.Hash("same password here", 1000), PasswordHasher.Hash("same password here", 1000));
    }

    [Fact]
    [Trait("req", "AC-01")]
    public void A_hash_made_with_fewer_iterations_than_the_setting_is_reported_stale_so_it_can_be_upgraded()
    {
        var old = PasswordHasher.Hash("a long enough password", 1000);

        Assert.True(PasswordHasher.Verify("a long enough password", old, 5000, out var stale));
        Assert.True(stale);
    }

    [Theory]
    [Trait("req", "AC-01")]
    [InlineData("")]
    [InlineData("pbkdf2-sha256$1000$AAAA")]
    [InlineData("pbkdf2-sha256$nope$AAAA$AAAA")]
    [InlineData("md5$1000$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$1000$!!!$!!!")]
    public void A_malformed_stored_value_never_matches_anything(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything at all", stored, 1000, out _));
    }

    [Theory]
    [Trait("req", "AC-05")]
    [InlineData("short", "at least")]
    [InlineData("password", "at least")]
    [InlineData("alice@example.com", "easy to guess")]
    [InlineData("            ", "only spaces")]
    public void A_poor_password_is_refused_with_a_reason(string password, string reason)
    {
        Assert.Contains(reason, PasswordPolicy.Problem(password, "alice@example.com"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("req", "AC-05")]
    public void A_long_password_is_accepted_and_one_beyond_the_limit_is_not()
    {
        Assert.Null(PasswordPolicy.Problem(new string('x', 12) + "-ok", "alice"));
        Assert.NotNull(PasswordPolicy.Problem(new string('x', PasswordPolicy.MaxLength + 1), "alice"));
    }

    [Fact]
    [Trait("req", "AC-06")]
    public void A_generated_password_is_long_readable_unique_and_acceptable_to_the_policy()
    {
        var made = Enumerable.Range(0, 50).Select(_ => PasswordPolicy.Generate()).ToList();

        Assert.Equal(50, made.Distinct().Count());
        Assert.All(made, p =>
        {
            Assert.Equal(24, p.Length);
            Assert.DoesNotContain(p, c => "0O1lI".Contains(c));
            Assert.Null(PasswordPolicy.Problem(p, "admin"));
        });
    }
}

/// <summary>Creating people, signing in with Basic or a session, and what ends a session.</summary>
public class AccountApiTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    [Fact]
    [Trait("req", "AC-01")]
    [Trait("req", "AC-04")]
    public async Task A_person_made_by_an_administrator_can_use_the_api_and_the_list_shows_them_without_any_secret()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);

        var read = await factory.ClientFor(name, password).GetAsync("/api/entities");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var me = await (await factory.ClientFor(name, password).GetAsync("/api/auth/me")).ReadJsonAsync();
        Assert.Equal(name, me.RootElement.GetProperty("username").GetString());
        Assert.Equal("User", me.RootElement.GetProperty("role").GetString());
        Assert.Equal("database", me.RootElement.GetProperty("source").GetString());
        Assert.Equal("basic", me.RootElement.GetProperty("via").GetString());

        var list = await (await factory.Admin().GetAsync("/api/admin/users")).Content.ReadAsStringAsync();
        Assert.Contains(name, list, StringComparison.Ordinal);
        Assert.Contains(CmsFactory.AdminName, list, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"config\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain(password, list, StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stamp", list, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("req", "AC-01")]
    public async Task The_password_is_stored_only_as_a_slow_salted_hash()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);

        var stored = (string)factory.Sql($"SELECT PasswordHash FROM Users WHERE Username = '{name}'")[0][0]!;

        Assert.StartsWith("pbkdf2-sha256$", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(password, stored, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("req", "AC-03")]
    public async Task A_temporary_password_must_be_replaced_before_anything_else_is_allowed()
    {
        var made = await factory.Admin().PostAsync("/api/admin/users", AccountHelpers.Json(new { username = AccountHelpers.NewName(), role = "User" }));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        using var body = await made.ReadJsonAsync();
        var name = body.RootElement.GetProperty("user").GetProperty("username").GetString()!;
        var temporary = body.RootElement.GetProperty("temporaryPassword").GetString()!;
        Assert.True(body.RootElement.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());

        var temp = factory.ClientFor(name, temporary);
        var blocked = await temp.GetAsync("/api/entities");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("password_change_required", await AccountHelpers.CodeAsync(blocked));
        using var me = await (await temp.GetAsync("/api/auth/me")).ReadJsonAsync();
        Assert.True(me.RootElement.GetProperty("mustChangePassword").GetBoolean());

        var replacement = AccountHelpers.NewPassword();
        var changed = await temp.PostAsync("/api/auth/password", AccountHelpers.Json(new { current = temporary, newPassword = replacement }));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, replacement).GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(name, temporary).GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-02")]
    public async Task Signing_in_gives_an_http_only_same_site_session_that_works_until_the_person_signs_out()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var browser = factory.CreateClient();

        var login = await browser.PostAsync("/api/auth/login", AccountHelpers.Json(new { username = name, password }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = string.Join(";", login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("lateral.session=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/entities")).StatusCode);
        using var me = await (await browser.GetAsync("/api/auth/me")).ReadJsonAsync();
        Assert.Equal("session", me.RootElement.GetProperty("via").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(AccountHelpers.FromPage(HttpMethod.Post, "/api/auth/logout"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-08")]
    public async Task A_session_cannot_be_used_to_write_without_the_header_a_hostile_page_cannot_add()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var browser = await AccountHelpers.SignedInAsync(factory, name, password);

        var forged = await browser.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        // Refusing the forged request did not sign the person out, and reading never needed the header.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(AccountHelpers.FromPage(HttpMethod.Post, "/api/auth/logout"))).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-02")]
    public async Task A_page_with_its_own_form_gets_a_plain_401_and_a_script_gets_the_browser_challenge()
    {
        var script = await factory.CreateClient().GetAsync("/api/entities");
        Assert.Equal(HttpStatusCode.Unauthorized, script.StatusCode);
        Assert.Contains("Basic", script.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        var page = await factory.CreateClient().SendAsync(AccountHelpers.FromPage(HttpMethod.Get, "/api/entities"));
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Empty(page.Headers.WwwAuthenticate);
    }

    [Theory]
    [Trait("req", "AC-08")]
    [InlineData("cross-site", HttpStatusCode.Forbidden)]
    [InlineData("same-site", HttpStatusCode.Forbidden)]
    [InlineData("same-origin", HttpStatusCode.OK)]
    [InlineData("none", HttpStatusCode.OK)]
    public async Task Another_site_cannot_sign_a_visitor_in_but_the_page_itself_and_scripts_can(string fetchSite, HttpStatusCode expected)
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = AccountHelpers.Json(new { username = name, password }) };
        request.Headers.Add("Sec-Fetch-Site", fetchSite);

        Assert.Equal(expected, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-02")]
    public async Task A_wrong_password_and_an_unknown_name_get_the_same_answer()
    {
        var (name, _) = await AccountHelpers.MakePersonAsync(factory);

        var wrong = await factory.CreateClient().PostAsync("/api/auth/login", AccountHelpers.Json(new { username = name, password = "not the password" }));
        var unknown = await factory.CreateClient().PostAsync("/api/auth/login", AccountHelpers.Json(new { username = "nobody@x.example", password = "not the password" }));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        // The bodies differ only by the request's trace id, so compare what they say.
        Assert.Equal(await AccountHelpers.CodeAsync(wrong), await AccountHelpers.CodeAsync(unknown));
        Assert.Equal(await AccountHelpers.DetailAsync(wrong), await AccountHelpers.DetailAsync(unknown));
        Assert.Empty(wrong.Headers.WwwAuthenticate);
    }

    [Fact]
    [Trait("req", "AC-02")]
    public async Task The_service_account_cannot_sign_in_to_a_session_and_a_configured_person_can_but_not_change_their_password()
    {
        var cms = await factory.CreateClient().PostAsync("/api/auth/login", AccountHelpers.Json(new { username = CmsFactory.CmsUser, password = CmsFactory.CmsPassword }));
        Assert.Equal(HttpStatusCode.Unauthorized, cms.StatusCode);

        var configured = await AccountHelpers.SignedInAsync(factory, CmsFactory.UserName, CmsFactory.UserPassword);
        Assert.Equal(HttpStatusCode.OK, (await configured.GetAsync("/api/entities")).StatusCode);

        var change = await configured.SendAsync(AccountHelpers.FromPage(
            HttpMethod.Post, "/api/auth/password", AccountHelpers.Json(new { current = CmsFactory.UserPassword, newPassword = AccountHelpers.NewPassword() })));
        Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
        Assert.Equal("managed_in_configuration", await AccountHelpers.CodeAsync(change));
    }

    [Fact]
    [Trait("req", "AC-02")]
    [Trait("req", "AC-04")]
    public async Task An_administrator_resetting_a_password_signs_the_person_out_everywhere()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var browser = await AccountHelpers.SignedInAsync(factory, name, password);
        var script = factory.ClientFor(name, password);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/api/entities")).StatusCode);

        var chosen = AccountHelpers.NewPassword();
        var reset = await factory.Admin().PostAsync($"/api/admin/users/{name}/password", AccountHelpers.Json(new { password = chosen }));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, chosen).GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-02")]
    [Trait("req", "AC-04")]
    public async Task Disabling_a_person_ends_their_session_and_remembered_basic_sign_in_at_once_and_enabling_lets_them_back()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var browser = await AccountHelpers.SignedInAsync(factory, name, password);
        var script = factory.ClientFor(name, password);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/api/entities")).StatusCode);

        var disabled = await factory.Admin().PatchAsync($"/api/admin/users/{name}", AccountHelpers.Json(new { disabled = true }));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/api/entities")).StatusCode);
        var login = await factory.CreateClient().PostAsync("/api/auth/login", AccountHelpers.Json(new { username = name, password }));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        await factory.Admin().PatchAsync($"/api/admin/users/{name}", AccountHelpers.Json(new { disabled = false }));
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, password).GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-02")]
    [Trait("req", "AC-04")]
    public async Task Revoking_sessions_signs_out_a_browser_but_keeps_the_password()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var browser = await AccountHelpers.SignedInAsync(factory, name, password);

        var revoked = await factory.Admin().PostAsync($"/api/admin/users/{name}/revoke-sessions", content: null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, password).GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-02")]
    public async Task A_change_made_straight_in_the_database_ends_a_remembered_sign_in()
    {
        // What the command-line tool does is write to the database from another process; the service notices the new stamp.
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var browser = await AccountHelpers.SignedInAsync(factory, name, password);
        var script = factory.ClientFor(name, password);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/api/entities")).StatusCode);

        // A new stamp alone ends the session but not the password, which is still right.
        factory.Sql($"UPDATE Users SET SecurityStamp = '{Guid.NewGuid()}' WHERE Username = '{name}'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/api/entities")).StatusCode);

        // A new password and stamp, as set-password writes them, ends the remembered Basic sign-in too.
        var other = AccountHelpers.NewPassword();
        factory.Sql($"UPDATE Users SET PasswordHash = '{PasswordHasher.Hash(other, 1000)}', SecurityStamp = '{Guid.NewGuid()}' WHERE Username = '{name}'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, other).GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-04")]
    public async Task A_role_change_applies_to_a_session_already_open()
    {
        // Someone else is an administrator, so the person under test is never the last one when they are demoted again.
        await AccountHelpers.MakePersonAsync(factory, "Admin");
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var browser = await AccountHelpers.SignedInAsync(factory, name, password);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/admin/users")).StatusCode);

        await factory.Admin().PatchAsync($"/api/admin/users/{name}", AccountHelpers.Json(new { role = "Admin" }));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/admin/users")).StatusCode);

        await factory.Admin().PatchAsync($"/api/admin/users/{name}", AccountHelpers.Json(new { role = "user" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-05")]
    public async Task A_person_changes_their_own_password_keeps_their_session_and_loses_every_other()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var here = await AccountHelpers.SignedInAsync(factory, name, password);
        var elsewhere = await AccountHelpers.SignedInAsync(factory, name, password);

        async Task<HttpResponseMessage> Change(string current, string next) =>
            await here.SendAsync(AccountHelpers.FromPage(HttpMethod.Post, "/api/auth/password", AccountHelpers.Json(new { current, newPassword = next })));

        var wrong = await Change("not my password", AccountHelpers.NewPassword());
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("wrong_password", await AccountHelpers.CodeAsync(wrong));
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(password, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(password, password)).StatusCode);

        var replacement = AccountHelpers.NewPassword();
        Assert.Equal(HttpStatusCode.OK, (await Change(password, replacement)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(name, password).GetAsync("/api/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(name, replacement).GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-04")]
    public async Task Nothing_in_the_audit_log_or_the_log_contains_a_password_but_every_change_is_recorded()
    {
        var chosen = AccountHelpers.NewPassword();
        var (name, _) = await AccountHelpers.MakePersonAsync(factory, password: chosen);
        var reset = await factory.Admin().PostAsync($"/api/admin/users/{name}/password", AccountHelpers.Json(new { }));
        using var body = await reset.ReadJsonAsync();
        var temporary = body.RootElement.GetProperty("temporaryPassword").GetString()!;
        await factory.Admin().PatchAsync($"/api/admin/users/{name}", AccountHelpers.Json(new { disabled = true }));
        await factory.Admin().DeleteAsync($"/api/admin/users/{name}");

        var audit = factory.Sql($"SELECT Actor, Action, Target, Detail FROM AuditLog WHERE Target = '{name}' ORDER BY Id");
        Assert.Equal(["user.create", "user.password.reset", "user.disable", "user.delete"], audit.Select(r => (string)r[1]!));
        Assert.All(audit, r => Assert.Equal(CmsFactory.AdminName, (string)r[0]!));

        var everything = string.Join('\n', audit.SelectMany(r => r.Select(c => c?.ToString())))
            + string.Join('\n', factory.Logs.Entries.Select(e => e.Message + e.Exception));
        Assert.DoesNotContain(chosen, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(temporary, everything, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("req", "AC-01")]
    public async Task A_hash_from_before_the_iteration_setting_was_raised_is_upgraded_at_the_next_sign_in()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var old = PasswordHasher.Hash(password, 1000);
        factory.Sql($"UPDATE Users SET PasswordHash = '{PasswordHasher.Hash(password, 10)}' WHERE Username = '{name}'");

        var login = await factory.CreateClient().PostAsync("/api/auth/login", AccountHelpers.Json(new { username = name, password }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var stored = (string)factory.Sql($"SELECT PasswordHash FROM Users WHERE Username = '{name}'")[0][0]!;
        Assert.StartsWith("pbkdf2-sha256$1000$", stored, StringComparison.Ordinal);
        Assert.NotEqual(old, stored);
    }
}

/// <summary>The rules that stop an administrator locking everyone out, and the ways a person can be refused at creation.</summary>
public class AccountGuardTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private Task<HttpResponseMessage> Create(string? username, string? role, string? password) =>
        factory.Admin().PostAsync("/api/admin/users", AccountHelpers.Json(new { username, role, password }));

    [Fact]
    [Trait("req", "AC-04")]
    public async Task The_last_enabled_administrator_cannot_be_removed_disabled_or_demoted_and_you_cannot_remove_yourself()
    {
        var (first, firstPassword) = await AccountHelpers.MakePersonAsync(factory, "Admin");

        var delete = await factory.Admin().DeleteAsync($"/api/admin/users/{first}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("last_administrator", await AccountHelpers.CodeAsync(delete));
        Assert.Equal(HttpStatusCode.Conflict, (await factory.Admin().PatchAsync($"/api/admin/users/{first}", AccountHelpers.Json(new { disabled = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await factory.Admin().PatchAsync($"/api/admin/users/{first}", AccountHelpers.Json(new { role = "User" }))).StatusCode);

        var (second, secondPassword) = await AccountHelpers.MakePersonAsync(factory, "Admin");
        var self = factory.ClientFor(second, secondPassword);
        var refused = await self.DeleteAsync($"/api/admin/users/{second}");
        Assert.Equal("not_yourself", await AccountHelpers.CodeAsync(refused));
        Assert.Equal(HttpStatusCode.Conflict, (await self.PatchAsync($"/api/admin/users/{second}", AccountHelpers.Json(new { disabled = true }))).StatusCode);

        // With another administrator in place the first can go.
        Assert.Equal(HttpStatusCode.NoContent, (await factory.ClientFor(second, secondPassword).DeleteAsync($"/api/admin/users/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(first, firstPassword).GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-04")]
    public async Task A_name_already_taken_a_bad_name_a_bad_role_and_a_weak_password_are_all_refused()
    {
        var (existing, _) = await AccountHelpers.MakePersonAsync(factory);

        Assert.Equal(HttpStatusCode.Conflict, (await Create(existing.ToUpperInvariant(), "User", AccountHelpers.NewPassword())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Create(CmsFactory.AdminName.ToUpperInvariant(), "User", AccountHelpers.NewPassword())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Create(CmsFactory.CmsUser, "User", AccountHelpers.NewPassword())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create("has:colon", "User", AccountHelpers.NewPassword())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create("", "User", AccountHelpers.NewPassword())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(new string('n', 101), "User", AccountHelpers.NewPassword())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(AccountHelpers.NewName(), "root", AccountHelpers.NewPassword())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(AccountHelpers.NewName(), "User", "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.Admin().PatchAsync($"/api/admin/users/{existing}", AccountHelpers.Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Admin().DeleteAsync("/api/admin/users/nobody-here")).StatusCode);
    }

    [Fact]
    [Trait("req", "AC-04")]
    public async Task Someone_who_is_not_an_administrator_cannot_manage_people_even_themselves()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(factory);
        var person = factory.ClientFor(name, password);

        Assert.Equal(HttpStatusCode.Forbidden, (await person.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await person.PatchAsync($"/api/admin/users/{name}", AccountHelpers.Json(new { role = "Admin" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await person.PostAsync($"/api/admin/users/{name}/password", AccountHelpers.Json(new { }))).StatusCode);
    }
}

/// <summary>Failed sign-ins are slowed per address and per username.</summary>
public class LoginThrottleTests : IClassFixture<LoginThrottleTests.ThrottledFactory>
{
    public sealed class ThrottledFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Auth:MaxFailuresPerMinute", "3")];
    }

    private readonly ThrottledFactory _factory;

    public LoginThrottleTests(ThrottledFactory factory)
    {
        _factory = factory;
        _factory.Time.Advance(TimeSpan.FromMinutes(5));
    }

    [Fact]
    [Trait("req", "AC-08")]
    public async Task Repeated_failed_sign_ins_are_answered_429_with_retry_after_even_for_the_right_password_until_the_minute_passes()
    {
        var (name, password) = await AccountHelpers.MakePersonAsync(_factory);
        var attacker = _factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.PostAsync("/api/auth/login", AccountHelpers.Json(new { username = name, password = "wrong password " + i }))).StatusCode);
        }

        var blocked = await attacker.PostAsync("/api/auth/login", AccountHelpers.Json(new { username = name, password }));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.RetryAfter is not null);

        _factory.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await attacker.PostAsync("/api/auth/login", AccountHelpers.Json(new { username = name, password }))).StatusCode);
    }
}
