using System.Net;
using System.Text.Json;
using Lateral.Cms.Auth;
using Lateral.Cms.Tests.Domain;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Api;

/// <summary>Findings from the security review that were small enough to close: each has a test that fails if it is undone.</summary>
public class HardeningTests
{
    private static string Messages(Exception? failure)
    {
        var text = new List<string>();
        for (var e = failure; e is not null; e = e.InnerException)
        {
            text.Add(e.Message);
        }

        return string.Join(" | ", text);
    }

    // ---- SH-01: no committed credentials -----------------------------------------------------------------

    private static WebApplicationBuilder Builder(string contentRoot, string environment) =>
        WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot, EnvironmentName = environment });

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "lateral-dev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    [Trait("req", "SH-01")]
    public void In_development_without_credentials_random_ones_are_generated_once_into_a_git_ignored_file_and_reused()
    {
        var folder = NewFolder();
        try
        {
            var first = Builder(folder, "Development");
            var path = DevelopmentCredentials.AddIfDevelopment(first);
            var password = first.Configuration["Auth:Cms:Passwords:0"];

            Assert.Equal(Path.Combine(folder, "App_Data", "dev-credentials.json"), path);
            Assert.True(File.Exists(path));
            Assert.Equal("cms-ingest-svc", first.Configuration["Auth:Cms:Username"]);
            Assert.True(Guid.TryParse(password, out _));
            Assert.True(Guid.TryParse(first.Configuration["Auth:Users:0:Password"], out _));
            Assert.Equal("Admin", first.Configuration["Auth:Users:1:Role"]);
            Assert.NotEqual(first.Configuration["Auth:Users:0:Password"], first.Configuration["Auth:Users:1:Password"]);

            // A second start reuses them: the accounts do not change under a running console.
            var second = Builder(folder, "Development");
            DevelopmentCredentials.AddIfDevelopment(second);
            Assert.Equal(password, second.Configuration["Auth:Cms:Passwords:0"]);

            // Deleting the file rotates them.
            File.Delete(path);
            var third = Builder(folder, "Development");
            DevelopmentCredentials.AddIfDevelopment(third);
            Assert.NotEqual(password, third.Configuration["Auth:Cms:Passwords:0"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [Trait("req", "SH-01")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Outside_development_nothing_is_generated_so_missing_credentials_still_stop_startup(string environment)
    {
        var folder = NewFolder();
        try
        {
            var builder = Builder(folder, environment);

            var path = DevelopmentCredentials.AddIfDevelopment(builder);

            Assert.Null(path);
            Assert.False(Directory.Exists(Path.Combine(folder, "App_Data")));
            Assert.Null(builder.Configuration["Auth:Cms:Username"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    [Trait("req", "SH-01")]
    public void Credentials_supplied_another_way_are_never_overridden_by_generated_ones()
    {
        var folder = NewFolder();
        try
        {
            var builder = Builder(folder, "Development");
            builder.Configuration["Auth:Cms:Username"] = "supplied-by-env";

            var path = DevelopmentCredentials.AddIfDevelopment(builder);

            Assert.Null(path);
            Assert.Equal("supplied-by-env", builder.Configuration["Auth:Cms:Username"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    [Trait("req", "SH-01")]
    public void No_settings_file_in_the_repository_holds_a_password_and_the_generated_file_is_git_ignored()
    {
        var root = DomainArchitectureTests.RepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src", "Lateral.Cms"), "appsettings*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var offending = new List<string>();
            void Walk(JsonElement element, string path)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject())
                        {
                            Walk(property.Value, $"{path}:{property.Name}");
                        }

                        break;
                    case JsonValueKind.Array:
                        var i = 0;
                        foreach (var item in element.EnumerateArray())
                        {
                            Walk(item, $"{path}:{i++}");
                        }

                        break;
                    case JsonValueKind.String when path.Contains("Password", StringComparison.OrdinalIgnoreCase) && element.GetString()!.Length > 0:
                        offending.Add(path);
                        break;
                }
            }

            Walk(document.RootElement, "");
            Assert.True(offending.Count == 0, $"{Path.GetFileName(file)} holds credentials at {string.Join(", ", offending)}");
        }

        var ignored = File.ReadAllText(Path.Combine(root, ".gitignore"));
        Assert.Contains("App_Data/", ignored);
    }

    [Fact]
    [Trait("req", "SH-01")]
    public void The_generated_file_has_the_shape_the_test_console_and_the_readme_read()
    {
        using var document = JsonDocument.Parse(DevelopmentCredentials.Generate());
        var auth = document.RootElement.GetProperty("Auth");

        Assert.InRange(auth.GetProperty("Cms").GetProperty("Username").GetString()!.Length, 10, 20);
        Assert.True(Guid.TryParse(auth.GetProperty("Cms").GetProperty("Passwords")[0].GetString(), out _));
        var users = auth.GetProperty("Users").EnumerateArray().ToList();
        Assert.Equal(["User", "Admin"], users.Select(u => u.GetProperty("Role").GetString()!));
        Assert.All(users, u => Assert.True(u.GetProperty("Password").GetString()!.Length >= 12));
        // The README's try-it takes the three passwords in this order from the file.
        var guids = System.Text.RegularExpressions.Regex.Matches(DevelopmentCredentials.Generate(), @"[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}");
        Assert.Equal(3, guids.Count);
    }

    // ---- SH-02: bounded memory on accept --------------------------------------------------------------------

    public sealed class OneAcceptAtATimeFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
            [new("Ingestion:MaxConcurrentAccepts", "1"), new("Ingestion:AcceptQueueLimit", "0")];
    }

    [Fact]
    [Trait("req", "SH-02")]
    public async Task Only_as_many_batches_are_read_into_memory_at_once_as_configured_and_the_rest_get_429_with_retry_after()
    {
        using var factory = new OneAcceptAtATimeFactory();
        _ = factory.Services;

        // Hold the write gate so a batch that gets past the limiter has to wait, keeping its permit. Send six at once: exactly one
        // can hold the only permit; the other five must be turned away immediately rather than queue behind it.
        List<Task<HttpResponseMessage>> sent;
        using (await factory.Services.GetRequiredService<Lateral.Cms.Persistence.WriteGate>().AcquireAsync(CancellationToken.None))
        {
            sent = [.. Enumerable.Range(0, 6).Select(_ => factory.Cms().PostAsync([Evt.Publish(Evt.Id(), 1, 1)]))];
            // Wait for the five rejections to arrive rather than for a fixed time: on a busy machine they take longer than any
            // short delay, and a fixed one made this test fail under load. Then give a sixth a moment to wrongly complete.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (sent.Count(s => s.IsCompleted) < 5 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            await Task.Delay(500);

            var turnedAway = sent.Where(s => s.IsCompleted).ToList();
            Assert.Equal(5, turnedAway.Count);
            Assert.All(turnedAway, s => Assert.Equal(HttpStatusCode.TooManyRequests, s.Result.StatusCode));
            Assert.All(turnedAway, s => Assert.NotNull(s.Result.Headers.RetryAfter));
        }

        // The one that held the permit is accepted once the gate is free, and the permit is then available again.
        var accepted = await Task.WhenAll(sent);
        Assert.Single(accepted, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Equal(HttpStatusCode.Accepted, (await factory.Cms().PostAsync([Evt.Publish(Evt.Id(), 1, 1)])).StatusCode);
        await factory.DrainAsync();
    }

    [Fact]
    [Trait("req", "SH-02")]
    public async Task With_the_defaults_a_burst_of_senders_is_queued_not_refused_so_nothing_is_lost()
    {
        using var factory = new CmsFactory();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 60).Select(async _ =>
            (await factory.Cms().PostAsync([Evt.Publish(Evt.Id(), 1, 1)])).StatusCode));

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Accepted, s));
        await factory.DrainAsync();
    }

    // ---- SH-03: the throttle and proxies --------------------------------------------------------------------

    private sealed class SetRemoteAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1"); // what the proxy, or the one client, looks like
                return nextMiddleware();
            });
            next(app);
        };
    }

    public class ThrottledFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Auth:MaxFailuresPerMinute", "3"), .. Proxy];

        protected virtual IEnumerable<KeyValuePair<string, string?>> Proxy => [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, SetRemoteAddress>());
        }

        public async Task<HttpStatusCode> BadSignInAsync(string forwardedFor)
        {
            var client = ClientFor(UserName, "wrong-password-123");
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/entities");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
            request.Headers.Authorization = CmsFactory.Basic(UserName, "wrong-password-123");
            _ = client;
            return (await CreateClient().SendAsync(request)).StatusCode;
        }
    }

    public sealed class BehindTrustedProxyFactory : ThrottledFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Proxy => [new("Proxy:KnownProxies:0", "127.0.0.1")];
    }

    [Fact]
    [Trait("req", "SH-03")]
    public async Task Behind_a_trusted_proxy_each_real_client_has_its_own_failure_count()
    {
        using var factory = new BehindTrustedProxyFactory();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await factory.BadSignInAsync("198.51.100.1"));
        }

        // The noisy client is throttled; a different client behind the same proxy is not.
        Assert.Equal(HttpStatusCode.TooManyRequests, await factory.BadSignInAsync("198.51.100.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await factory.BadSignInAsync("203.0.113.9"));
    }

    // ---- the routes anyone can reach cost little ------------------------------------------------------------

    [Fact]
    [Trait("req", "SH-03")]
    public async Task The_sign_in_route_refuses_a_giant_body_and_a_giant_password_before_hashing_anything()
    {
        using var factory = new CmsFactory();
        var anonymous = factory.CreateClient();

        var longPassword = await anonymous.PostAsync("/api/auth/login", Evt.Json(new { username = "someone", password = new string('x', 300) }));
        Assert.Equal(HttpStatusCode.BadRequest, longPassword.StatusCode);

        var huge = await anonymous.PostAsync("/api/auth/login", Evt.Json(new { username = "someone", password = new string('x', 64 * 1024) }));
        Assert.True(huge.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, $"got {(int)huge.StatusCode}");
    }

    [Fact]
    [Trait("req", "SH-03")]
    public async Task The_health_route_answers_and_does_not_announce_the_server()
    {
        using var factory = new CmsFactory();

        var first = await factory.CreateClient().GetAsync("/health");
        var second = await factory.CreateClient().GetAsync("/health"); // from the kept answer

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(first.Headers.Contains("Server"));
    }

    public sealed class CmsFromOneNetworkFactory : ThrottledFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Proxy =>
            [new("Proxy:KnownProxies:0", "127.0.0.1"), new("Auth:Cms:AllowedAddresses:0", "203.0.113.0/24")];
    }

    [Fact]
    [Trait("req", "SH-03")]
    public async Task The_CMS_credential_is_refused_from_outside_the_network_it_is_allowed_from()
    {
        using var factory = new CmsFromOneNetworkFactory();

        async Task<HttpStatusCode> CmsCallFrom(string address)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/cms/batches/{Guid.NewGuid()}");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", address);
            request.Headers.Authorization = CmsFactory.Basic(CmsFactory.CmsUser, CmsFactory.CmsPassword);
            return (await factory.CreateClient().SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotFound, await CmsCallFrom("203.0.113.9")); // allowed: through to a batch that does not exist
        Assert.Equal(HttpStatusCode.Unauthorized, await CmsCallFrom("198.51.100.1")); // the right credential, the wrong place
    }

    [Fact]
    [Trait("req", "SH-03")]
    public async Task Without_a_trusted_proxy_a_forged_forwarded_for_header_cannot_dodge_the_throttle()
    {
        using var factory = new ThrottledFactory();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await factory.BadSignInAsync($"198.51.100.{i}"));
        }

        // A fresh address in the header makes no difference: the header is ignored and the connection is what counts.
        Assert.Equal(HttpStatusCode.TooManyRequests, await factory.BadSignInAsync("203.0.113.77"));
    }

    private sealed class BadProxyFactory(string key, string value) : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new(key, value)];
    }

    [Theory]
    [Trait("req", "SH-03")]
    [InlineData("Proxy:KnownProxies:0", "not-an-address", "Proxy:KnownProxies")]
    [InlineData("Proxy:KnownNetworks:0", "10.0.0.0", "Proxy:KnownNetworks")]
    [InlineData("Proxy:KnownNetworks:0", "10.0.0.0/99", "Proxy:KnownNetworks")]
    [InlineData("Auth:Cms:AllowedAddresses:0", "not-an-address", "Auth:Cms:AllowedAddresses")]
    public void An_invalid_proxy_setting_stops_startup_and_names_itself(string key, string value, string expected)
    {
        using var factory = new BadProxyFactory(key, value);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains(expected, Messages(failure));
    }

    // ---- SH-04: the reset guard is an allow-list --------------------------------------------------------------

    private sealed class EnvironmentResetFactory(string environment, string? acknowledged) : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
            [new("Admin:AllowReset", "true"), new("Admin:ResetAllowedEnvironment", acknowledged)];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment(environment);
        }
    }

    [Theory]
    [Trait("req", "SH-04")]
    [InlineData("Staging", null)]
    [InlineData("QA", null)]
    [InlineData("Whatever", null)]
    [InlineData("Staging", "Production")]
    [InlineData("Staging", "")]
    [InlineData("Production", "Production")]
    public void The_reset_is_refused_in_every_environment_that_has_not_been_named_on_purpose_and_always_in_production(string environment, string? acknowledged)
    {
        using var factory = new EnvironmentResetFactory(environment, acknowledged);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("Admin:AllowReset", Messages(failure));
    }

    [Theory]
    [Trait("req", "SH-04")]
    [InlineData("Development", null)]
    [InlineData("Staging", "Staging")]
    [InlineData("QA", "qa")]
    public async Task The_reset_is_allowed_in_development_or_in_an_environment_named_on_purpose(string environment, string? acknowledged)
    {
        using var factory = new EnvironmentResetFactory(environment, acknowledged);

        var response = await factory.Admin().PostAsync("/api/admin/reset", Evt.Raw("""{"confirm":"nope"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // mapped, and asks for the confirmation phrase
    }

    // ---- SH-05: audit of admin actions ------------------------------------------------------------------------

    [Fact]
    [Trait("req", "SH-05")]
    public async Task Disabling_and_enabling_an_entity_are_audited_once_each_with_the_admins_name_and_repeats_add_nothing()
    {
        using var factory = new CmsFactory();
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        await factory.Admin().PostAsync($"/api/entities/{id}/disable", null);
        await factory.Admin().PostAsync($"/api/entities/{id}/disable", null); // idempotent: no second record
        await factory.Admin().PostAsync($"/api/entities/{id}/enable", null);
        await factory.Admin().PostAsync($"/api/entities/{id}/enable", null);
        await factory.Admin().PostAsync($"/api/entities/{Evt.Id()}/disable", null); // unknown id: nothing changed, nothing recorded

        var rows = factory.Sql($"SELECT Action, Actor FROM AuditLog WHERE Target = '{id}' ORDER BY Id");
        Assert.Equal(["entity.disabled", "entity.enabled"], rows.Select(r => (string)r[0]!));
        Assert.All(rows, r => Assert.Equal(CmsFactory.AdminName, (string)r[1]!));
    }

    public sealed class ResettableFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
            [new("Admin:AllowReset", "true"), new("Admin:ResetAllowedEnvironment", "Testing")];
    }

    [Fact]
    [Trait("req", "SH-05")]
    public async Task A_reset_is_audited_and_never_erases_the_audit_log()
    {
        using var factory = new ResettableFactory();
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));
        await factory.Admin().PostAsync($"/api/entities/{id}/disable", null);
        var before = factory.Count("SELECT COUNT(*) FROM AuditLog");

        var response = await factory.Admin().PostAsync("/api/admin/reset", Evt.Raw("""{"confirm":"reset-all-data"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, factory.Count("SELECT COUNT(*) FROM Entities"));
        Assert.Equal(before + 1, factory.Count("SELECT COUNT(*) FROM AuditLog"));
        Assert.Equal("admin.reset", (string)factory.Sql("SELECT Action FROM AuditLog ORDER BY Id DESC LIMIT 1")[0][0]!);
        Assert.Equal(1, factory.Count($"SELECT COUNT(*) FROM AuditLog WHERE Action = 'entity.disabled' AND Target = '{id}'"));
    }

    // ---- SH-06: failed batches are visible -------------------------------------------------------------------

    [Fact]
    [Trait("req", "SH-06")]
    public async Task The_failed_bytes_are_reported_so_set_aside_batches_cannot_quietly_fill_the_queue()
    {
        using var factory = new CmsFactory();
        _ = factory.Services; // start the app so the database exists
        var poison = "poison-" + Guid.NewGuid().ToString("N");
        factory.Sql($"CREATE TRIGGER \"fail_{poison}\" BEFORE INSERT ON Entities WHEN NEW.Id = '{poison}' BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;");
        using var ack = await factory.AcceptAsync(Evt.Publish(poison, 1, 1, new { title = new string('x', 2_000) }));
        for (var i = 0; i < 3; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(5));
            await factory.StepAsync();
        }

        using var report = JsonDocument.Parse(await factory.Admin().GetStringAsync("/api/admin/ingestion"));
        await factory.RefreshBacklogAsync();
        var backlog = factory.Services.GetRequiredService<Lateral.Cms.Ingestion.QueueBacklog>();

        Assert.Equal(1, report.RootElement.GetProperty("failedBatches").GetInt32());
        Assert.True(report.RootElement.GetProperty("failedBytes").GetInt64() >= 2_000);
        Assert.Equal(report.RootElement.GetProperty("failedBytes").GetInt64(), backlog.FailedBytes);
        Assert.True(report.RootElement.GetProperty("usedBytes").GetInt64() >= report.RootElement.GetProperty("failedBytes").GetInt64());
    }
}
