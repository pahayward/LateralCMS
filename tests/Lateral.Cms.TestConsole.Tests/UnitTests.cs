using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.TestConsole.Tests;

public class CmsTargetTests
{
    private static readonly Uri Base = new("http://cms.test");

    [Theory]
    [InlineData("cms/events", null, "http://cms.test/cms/events")]
    [InlineData("health", null, "http://cms.test/health")]
    [InlineData("api/entities", "?limit=2&cursor=abc", "http://cms.test/api/entities?limit=2&cursor=abc")]
    [InlineData("api/entities/article-1", null, "http://cms.test/api/entities/article-1")]
    [InlineData("api/entities/article-1/disable", null, "http://cms.test/api/entities/article-1/disable")]
    [InlineData("API/Entities/x", null, "http://cms.test/API/Entities/x")]
    [InlineData("api/admin/reset", null, "http://cms.test/api/admin/reset")]
    [InlineData("cms/batches/6f0c8f64-0000-4000-8000-000000000001", null, "http://cms.test/cms/batches/6f0c8f64-0000-4000-8000-000000000001")]
    [InlineData("api/admin/ingestion", null, "http://cms.test/api/admin/ingestion")]
    [InlineData("api/admin/ingestion/6f0c8f64-0000-4000-8000-000000000001/requeue", null, "http://cms.test/api/admin/ingestion/6f0c8f64-0000-4000-8000-000000000001/requeue")]
    public void The_cms_routes_resolve_against_the_configured_host(string path, string? query, string expected)
    {
        Assert.True(CmsTarget.TryBuild(Base, path, query, out var target, out _));
        Assert.Equal(expected, target.AbsoluteUri);
    }

    [Theory]
    [InlineData("a b", "http://cms.test/api/entities/a%20b")]
    [InlineData("100%", "http://cms.test/api/entities/100%25")]
    [InlineData("naïve", "http://cms.test/api/entities/na%C3%AFve")]
    [InlineData("a?b#c", "http://cms.test/api/entities/a%3Fb%23c")]
    public void Ids_are_escaped_so_they_stay_one_path_segment(string id, string expected)
    {
        Assert.True(CmsTarget.TryBuild(Base, "api/entities/" + id, null, out var target, out _));
        Assert.Equal(expected, target.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("swagger/index.html")]
    [InlineData("api/entitiesx")]
    [InlineData("api/admin")]
    [InlineData("api/admin/users")]
    [InlineData("api/admin/resetx")]
    [InlineData("api/admin/ingestionx")]
    [InlineData("cms/batchesx")]
    [InlineData("cms")]
    [InlineData("api")]
    [InlineData("api/entities/")]
    [InlineData("api//entities")]
    [InlineData("api/entities/../../secret")]
    [InlineData("api/entities/./x")]
    [InlineData("../cms/events")]
    [InlineData("//evil.example/api/entities")]
    [InlineData("http://evil.example/api/entities")]
    [InlineData("api/entities/a\\b")]
    [InlineData("api/entities/a\nb")]
    public void Anything_else_is_refused(string? path)
    {
        Assert.False(CmsTarget.TryBuild(Base, path, null, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void The_host_never_changes_and_a_base_path_is_kept()
    {
        Assert.True(CmsTarget.TryBuild(new Uri("https://gateway.test:8443/cms-root"), "cms/events", null, out var target, out _));
        Assert.Equal("https://gateway.test:8443/cms-root/cms/events", target.AbsoluteUri);
    }
}

public class LocalRequestGuardTests
{
    private static HttpRequest Request(string host, Action<IHeaderDictionary>? headers = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Headers["X-Console-Request"] = "1";
        headers?.Invoke(context.Request.Headers);
        return context.Request;
    }

    [Theory]
    [InlineData("localhost:5100")]
    [InlineData("127.0.0.1:5100")]
    [InlineData("[::1]:5100")]
    public void Loopback_names_are_allowed(string host) => Assert.Null(LocalRequestGuard.Refusal(Request(host)));

    [Theory]
    [InlineData("evil.example")]
    [InlineData("localhost.evil.example:5100")]
    [InlineData("192.168.1.20:5100")]
    public void Any_other_name_is_refused_even_if_it_resolves_to_this_machine(string host) =>
        Assert.NotNull(LocalRequestGuard.Refusal(Request(host)));

    [Fact]
    public void The_console_header_is_required() =>
        Assert.NotNull(LocalRequestGuard.Refusal(Request("localhost:5100", h => h.Remove("X-Console-Request"))));

    [Fact]
    public void An_origin_must_be_the_console_itself()
    {
        Assert.Null(LocalRequestGuard.Refusal(Request("localhost:5100", h => h.Origin = "http://localhost:5100")));
        Assert.NotNull(LocalRequestGuard.Refusal(Request("localhost:5100", h => h.Origin = "http://localhost:6000")));
        Assert.NotNull(LocalRequestGuard.Refusal(Request("localhost:5100", h => h.Origin = "null")));
    }
}

public class ChecksumVerifierTests
{
    // The value the CMS README publishes for {"title":"Hello"}.
    private const string HelloSha256 = "sha256:5c924960820d681cb4ac7c1dd76c1b56b963dbbf718faba003691dd26cce3e38";

    [Fact]
    public void A_correct_sha256_matches()
    {
        var check = ChecksumVerifier.Check(HelloSha256, """{"title":"Hello"}""");

        Assert.True(check.Match);
        Assert.Equal("sha256", check.Algorithm);
        Assert.Equal(HelloSha256, check.Computed);
    }

    [Theory]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")] // well-known empty-input values
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    public void The_known_empty_input_vectors_match(string claimed) =>
        Assert.True(ChecksumVerifier.Check(claimed, "").Match);

    [Fact]
    public void Any_change_to_the_payload_is_a_mismatch()
    {
        var check = ChecksumVerifier.Check(HelloSha256, """{"title":"hello"}""");

        Assert.False(check.Match);
        Assert.NotEqual(HelloSha256, check.Computed);
    }

    [Theory]
    [InlineData("sha1:da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [InlineData("nonsense")]
    [InlineData("")]
    public void An_unknown_algorithm_never_matches(string claimed)
    {
        var check = ChecksumVerifier.Check(claimed, "");

        Assert.False(check.Match);
        Assert.Equal("", check.Computed);
    }
}

public class AccountBookTests
{
    private const string CmsSettings = """
        {
          // comments and trailing commas are fine in the CMS's settings
          "Auth": {
            "Cms": { "Username": "cms-name", "Passwords": [ "cms-pass-1", "cms-pass-2", ], },
            "Users": [
              { "Username": "u1", "Password": "p1", "Role": "User" },
              { "Username": "a1", "Password": "pa", "Role": "admin" },
              { "Username": "u2", "Password": "p2", "Role": "User" },
            ],
          },
        }
        """;

    [Fact]
    public void The_cms_settings_yield_one_account_per_role()
    {
        var accounts = AccountBook.ReadCmsSettings(CmsSettings);

        Assert.Equal(("cms-name", "cms-pass-1"), accounts["cms"]);
        Assert.Equal(("u1", "p1"), accounts["user"]);
        Assert.Equal(("a1", "pa"), accounts["admin"]);
    }

    [Fact]
    public void Settings_without_an_auth_section_yield_nothing() =>
        Assert.Empty(AccountBook.ReadCmsSettings("""{ "Logging": {} }"""));

    [Fact]
    public void The_repositorys_own_development_settings_still_provide_all_three_accounts()
    {
        // Guards the one coupling this tool has to the CMS: where its development accounts live.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lateral.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var accounts = AccountBook.ReadCmsSettings(File.ReadAllText(
            Path.Combine(directory.FullName, "src", "Lateral.Cms", "appsettings.Development.json")));

        Assert.Equal(["admin", "cms", "user"], accounts.Keys.Order());
    }

    [Fact]
    public void Development_reads_the_cms_file_and_explicit_accounts_win()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "cms.json"), CmsSettings);
        var options = Options.Create(new ConsoleOptions
        {
            CmsDevSettingsPath = "cms.json",
            Accounts = { ["user"] = new AccountOptions { Username = "override", Password = "secret" } },
        });

        var book = new AccountBook(options, new FakeEnvironment("Development", directory.Path));

        Assert.Equal(["admin", "cms", "user"], book.Configured.Order());
        Assert.Equal(AccountBook.Basic("override", "secret"), book.BasicHeader("user"));
        Assert.Equal(AccountBook.Basic("cms-name", "cms-pass-1"), book.BasicHeader("cms"));
    }

    [Fact]
    public void Outside_development_the_cms_file_is_ignored()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "cms.json"), CmsSettings);

        var book = new AccountBook(Options.Create(new ConsoleOptions { CmsDevSettingsPath = "cms.json" }), new FakeEnvironment("Production", directory.Path));

        Assert.Empty(book.Configured);
        Assert.Null(book.BasicHeader("cms"));
    }

    [Fact]
    public void Half_filled_accounts_are_not_accounts()
    {
        var options = Options.Create(new ConsoleOptions { Accounts = { ["admin"] = new AccountOptions { Username = "only-a-name" } } });

        Assert.Empty(new AccountBook(options, new FakeEnvironment("Production", ".")).Configured);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("console-tests-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FakeEnvironment(string name, string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
