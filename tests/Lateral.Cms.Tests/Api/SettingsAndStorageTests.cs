using System.Net;
using System.Text.Json;
using Lateral.Cms.Admin;
using Lateral.Cms.Tests.Auth;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Api;

/// <summary>What the service is configured with and where it keeps things, for the administrator.</summary>
public class SettingsAndStorageTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private static List<JsonElement> Settings(JsonDocument report) =>
        report.RootElement.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("settings").EnumerateArray()).ToList();

    [Fact]
    [Trait("req", "CF-01")]
    public void Every_setting_the_service_reads_is_described_and_nothing_is_described_that_it_does_not_read()
    {
        var (described, host, groups) = SettingsReport.CatalogueKeys();

        // A setting added to an options class without a line in settings.json fails here, so the page cannot fall behind the code.
        Assert.Equal(SettingsReport.OptionKeys().Order(StringComparer.Ordinal), described.Order(StringComparer.Ordinal));
        Assert.Equal(described.Count, described.Distinct().Count());
        Assert.Contains("Urls", host);
        Assert.NotEmpty(groups);
    }

    [Fact]
    [Trait("req", "CF-01")]
    public async Task The_report_gives_each_setting_its_value_default_source_and_environment_variable()
    {
        var response = await factory.Admin().GetAsync("/api/admin/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var report = await response.ReadJsonAsync();
        var all = Settings(report);

        Assert.Equal("Testing", report.RootElement.GetProperty("environment").GetString());
        Assert.Equal(SettingsReport.CatalogueKeys().Keys.Count + SettingsReport.CatalogueKeys().HostKeys.Count, all.Count);
        Assert.All(all, s => Assert.False(string.IsNullOrWhiteSpace(s.GetProperty("description").GetString())));

        var path = all.Single(s => s.GetProperty("key").GetString() == "Database:ConnectionString");
        Assert.Equal("set in code", path.GetProperty("source").GetString());
        Assert.False(path.GetProperty("isDefault").GetBoolean());
        Assert.Equal("Database__ConnectionString", path.GetProperty("env").GetString());

        var fromFile = all.Single(s => s.GetProperty("key").GetString() == "Api:MaxPageSize");
        Assert.Equal("file appsettings.json", fromFile.GetProperty("source").GetString());

        var untouched = all.Single(s => s.GetProperty("key").GetString() == "Assets:MaxParts");
        Assert.True(untouched.GetProperty("isDefault").GetBoolean());
        Assert.Equal("default", untouched.GetProperty("source").GetString());
        Assert.Equal(10_000, untouched.GetProperty("value").GetInt32());

        var span = all.Single(s => s.GetProperty("key").GetString() == "Inbox:RetryMaxDelay");
        Assert.Equal("00:01:00", span.GetProperty("value").GetString());
    }

    [Fact]
    [Trait("req", "CF-01")]
    public async Task Secrets_are_reported_only_as_set_or_not_set()
    {
        var text = await (await factory.Admin().GetAsync("/api/admin/settings")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(CmsFactory.CmsPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CmsFactory.CmsRotatedPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CmsFactory.AdminPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CmsFactory.UserPassword, text, StringComparison.Ordinal);

        using var report = JsonDocument.Parse(text);
        var passwords = Settings(report).Single(s => s.GetProperty("key").GetString() == "Auth:Cms:Passwords");
        Assert.Equal("set", passwords.GetProperty("value").GetString());
        Assert.True(passwords.GetProperty("secret").GetBoolean());
    }

    [Fact]
    [Trait("req", "CF-01")]
    public async Task A_password_inside_a_connection_string_is_hidden()
    {
        using var secretive = new SecretiveFactory();
        var text = await (await secretive.Admin().GetAsync("/api/admin/settings")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("hunter2-secret", text, StringComparison.Ordinal);
        Assert.Contains("Password=***", text, StringComparison.Ordinal);
    }

    private sealed class SecretiveFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
            [new("Database:ReadConnectionString", $"Data Source={DatabasePath};Password=hunter2-secret")];
    }

    [Fact]
    [Trait("req", "CF-02")]
    public async Task The_storage_report_says_where_everything_is_kept_how_big_it_is_and_what_to_back_up()
    {
        // Put something in the asset store so there is something to count.
        var upload = await factory.Cms().PutAsync("/cms/assets/storage-report.txt", new StringContent("hello storage"));
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);

        var response = await factory.Admin().GetAsync("/api/admin/storage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var report = await response.ReadJsonAsync();
        var root = report.RootElement;

        Assert.Equal(factory.DatabasePath, root.GetProperty("database").GetProperty("path").GetString());
        Assert.True(root.GetProperty("database").GetProperty("bytes").GetInt64() > 0);
        Assert.Equal(factory.AssetsPath, root.GetProperty("assets").GetProperty("root").GetString());
        Assert.True(root.GetProperty("assets").GetProperty("storedBytes").GetInt64() >= 13);
        Assert.True(root.GetProperty("assets").GetProperty("quotaBytes").GetInt64() > 0);
        Assert.Equal(factory.KeysPath, root.GetProperty("sessionKeys").GetProperty("path").GetString());
        Assert.True(root.GetProperty("counts").GetProperty("assets").GetInt32() >= 1);
        Assert.Equal(4, root.GetProperty("backup").GetArrayLength());
        Assert.Equal(3, root.GetProperty("assets").GetProperty("folders").GetArrayLength());
        Assert.Contains("standard output", root.GetProperty("logs").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("req", "CF-02")]
    public async Task The_storage_report_contains_no_credentials()
    {
        var text = await (await factory.Admin().GetAsync("/api/admin/storage")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(CmsFactory.CmsPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CmsFactory.AdminPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2", text, StringComparison.OrdinalIgnoreCase);
    }
}
