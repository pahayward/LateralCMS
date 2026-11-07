using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lateral.Cms.TestConsole.Tests;

public class ConsoleApiTests : IClassFixture<ConsoleFactory>
{
    private readonly ConsoleFactory _factory;

    public ConsoleApiTests(ConsoleFactory factory)
    {
        _factory = factory;
        _factory.Cms.Reset();
    }

    // ------------------------------------------------------------ the guard

    [Fact]
    public async Task A_request_without_the_console_header_is_refused_before_anything_is_proxied()
    {
        var response = await _factory.CreateClient().GetAsync("/api/cms/api/entities");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    [InlineData("Origin", "http://evil.example")]
    [InlineData("Origin", "http://localhost:9999")]
    public async Task A_request_from_another_site_is_refused(string header, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/cms/api/entities");
        request.Headers.Add("X-Console-Request", "1");
        request.Headers.Add("X-Console-Act-As", "user");
        request.Headers.Add(header, value);

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    [Fact]
    public async Task A_same_origin_page_is_allowed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/cms/health");
        request.Headers.Add("X-Console-Request", "1");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Origin", "http://localhost");

        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_request_addressed_to_any_other_name_is_refused()
    {
        // DNS rebinding: a hostile name that resolves to this machine must not get an answer.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/cms/api/entities");
        request.Headers.Host = "evil.example";
        request.Headers.Add("X-Console-Request", "1");
        request.Headers.Add("X-Console-Act-As", "user");

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    // ------------------------------------------------------------ credentials

    [Fact]
    public async Task The_console_adds_the_credentials_of_the_chosen_account()
    {
        await _factory.Page().ProxyAsync("api/entities?limit=2", "user");

        var sent = Assert.Single(_factory.Cms.Requests);
        Assert.Equal(ConsoleFactory.Basic(ConsoleFactory.UserName, ConsoleFactory.UserPassword), sent.Authorization);
        Assert.Equal("http://cms.test/api/entities?limit=2", sent.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Anonymous_sends_no_credentials_and_a_browser_supplied_header_is_not_passed_on()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/cms/api/entities");
        request.Headers.Add("X-Console-Act-As", "anonymous");
        request.Headers.Add("Authorization", ConsoleFactory.Basic("someone", "else"));

        await _factory.Page().SendAsync(request);

        Assert.Null(Assert.Single(_factory.Cms.Requests).Authorization);
    }

    [Fact]
    public async Task Wrong_sends_a_real_username_with_a_different_password()
    {
        await _factory.Page().ProxyAsync("api/entities", "wrong");

        var header = Assert.Single(_factory.Cms.Requests).Authorization!;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
        Assert.StartsWith(ConsoleFactory.UserName + ":", decoded);
        Assert.NotEqual(ConsoleFactory.UserName + ":" + ConsoleFactory.UserPassword, decoded);
    }

    [Fact]
    public async Task Custom_credentials_are_passed_through_as_basic_auth()
    {
        var custom = Convert.ToBase64String(Encoding.UTF8.GetBytes("typed:in"));

        await _factory.Page().ProxyAsync("api/entities", "custom", custom: custom);

        Assert.Equal("Basic " + custom, Assert.Single(_factory.Cms.Requests).Authorization);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("nobody")]
    public async Task An_account_that_is_not_configured_is_refused_not_guessed(string account)
    {
        var response = await _factory.Page().ProxyAsync("api/entities", account);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    [Fact]
    public async Task Custom_without_credentials_is_refused()
    {
        var response = await _factory.Page().ProxyAsync("api/entities", "custom");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    [Fact]
    public async Task No_secret_is_ever_sent_to_the_browser()
    {
        var page = _factory.Page();
        var responses = new[]
        {
            await page.GetAsync("/api/config"),
            await page.ProxyAsync("api/entities", "user"),
            await page.ProxyAsync("api/entities", "wrong"),
            await page.GetAsync("/api/verify/abc?as=user"),
        };

        foreach (var response in responses)
        {
            var everything = string.Join('\n', response.Headers.Select(h => $"{h.Key}: {string.Join(',', h.Value)}"))
                             + await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(ConsoleFactory.CmsPassword, everything);
            Assert.DoesNotContain(ConsoleFactory.UserPassword, everything);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ConsoleFactory.UserName}:{ConsoleFactory.UserPassword}")), everything);
        }
    }

    // ------------------------------------------------------------ the proxy

    [Theory]
    [InlineData("admin/secret")]
    [InlineData("api/entities/../../etc")]
    [InlineData("swagger")]
    public async Task Only_the_cms_endpoints_can_be_reached(string path)
    {
        var response = await _factory.Page().ProxyAsync(path, "user");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    [Fact]
    public async Task The_status_body_and_useful_headers_of_the_answer_come_back_unchanged()
    {
        _factory.Cms.Respond = _ =>
        {
            var answer = Answer(HttpStatusCode.TooManyRequests, """{"title":"Too Many Requests"}""");
            answer.Headers.Add("Retry-After", "42");
            answer.Headers.Add("X-Correlation-Id", "batch-123");
            answer.Headers.Add("Set-Cookie", "session=abc");
            return answer;
        };

        var response = await _factory.Page().ProxyAsync("cms/events", "cms", HttpMethod.Post, new StringContent("[]", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("42", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal("batch-123", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("Too Many Requests", await response.Content.ReadAsStringAsync());
        Assert.StartsWith("application/json", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task A_post_body_and_content_type_reach_the_cms_untouched()
    {
        const string batch = """[ { "type": "publish", "id": "a", "version": 1 } ]""";

        await _factory.Page().ProxyAsync("cms/events", "cms", HttpMethod.Post, new StringContent(batch, Encoding.UTF8, "application/json"));

        var sent = Assert.Single(_factory.Cms.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(batch, sent.Body);
        Assert.StartsWith("application/json", sent.ContentType);
        Assert.Equal(Encoding.UTF8.GetByteCount(batch), sent.ContentLength);
    }

    [Fact]
    public async Task The_reset_route_is_reachable_with_its_confirmation_body_intact()
    {
        const string confirmation = """{"confirm":"reset-all-data"}""";
        var custom = Convert.ToBase64String(Encoding.UTF8.GetBytes("typed:in"));

        await _factory.Page().ProxyAsync(
            "api/admin/reset", "custom", HttpMethod.Post, new StringContent(confirmation, Encoding.UTF8, "application/json"), custom);

        var sent = Assert.Single(_factory.Cms.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://cms.test/api/admin/reset", sent.Uri.AbsoluteUri);
        Assert.Equal(confirmation, sent.Body);
    }

    [Fact]
    public async Task An_unreachable_cms_is_a_502_that_names_it()
    {
        _factory.Cms.Throw = new HttpRequestException("connection refused");

        var response = await _factory.Page().ProxyAsync("health", "anonymous");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var json = await response.ReadJsonAsync();
        Assert.Contains("http://cms.test", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_config_lists_account_names_only()
    {
        using var json = await (await _factory.Page().GetAsync("/api/config")).ReadJsonAsync();

        Assert.Equal(["cms", "user"], json.RootElement.GetProperty("accounts").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("http://cms.test", json.RootElement.GetProperty("cmsBaseUrl").GetString());
        Assert.Equal(5 * 1024 * 1024, json.RootElement.GetProperty("limits").GetProperty("maxBodyBytes").GetInt64());
    }

    // ------------------------------------------------------------ checksum verification

    private const string HelloSha256 = "sha256:5c924960820d681cb4ac7c1dd76c1b56b963dbbf718faba003691dd26cce3e38";

    private static string Entity(string checksum) =>
        """{"id":"abc","version":3,"checksum":"CHECKSUM","updatedAt":"2026-01-01T00:00:00Z","payload":{"title":"Hello"}}"""
            .Replace("CHECKSUM", checksum);

    [Fact]
    public async Task Verify_recomputes_the_checksum_over_the_payload_as_served()
    {
        _factory.Cms.Respond = _ => Answer(HttpStatusCode.OK, Entity(HelloSha256));

        using var json = await (await _factory.Page().GetAsync("/api/verify/abc?as=user")).ReadJsonAsync();

        Assert.True(json.RootElement.GetProperty("match").GetBoolean());
        Assert.Equal(3, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(17, json.RootElement.GetProperty("payloadBytes").GetInt32());
        Assert.Equal("http://cms.test/api/entities/abc", Assert.Single(_factory.Cms.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Verify_reports_a_mismatch_rather_than_trusting_the_claim()
    {
        _factory.Cms.Respond = _ => Answer(HttpStatusCode.OK, Entity("sha256:" + new string('0', 64)));

        using var json = await (await _factory.Page().GetAsync("/api/verify/abc?as=user")).ReadJsonAsync();

        Assert.False(json.RootElement.GetProperty("match").GetBoolean());
        Assert.Equal(HelloSha256, json.RootElement.GetProperty("computed").GetString());
    }

    [Fact]
    public async Task Verify_says_so_when_the_cms_does_not_show_the_entity_to_that_account()
    {
        _factory.Cms.Respond = _ => Answer(HttpStatusCode.NotFound, """{"status":404}""");

        using var json = await (await _factory.Page().GetAsync("/api/verify/hidden?as=user")).ReadJsonAsync();

        Assert.False(json.RootElement.GetProperty("visible").GetBoolean());
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Verify_only_acts_as_a_user_or_an_admin()
    {
        var response = await _factory.Page().GetAsync("/api/verify/abc?as=cms");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    // ------------------------------------------------------------ the oversize probe

    private const long TenGiB = 10L * 1024 * 1024 * 1024;

    [Fact]
    public async Task A_declared_ten_gigabyte_body_is_offered_to_the_cms_with_its_length_and_nothing_is_read()
    {
        _factory.Cms.Respond = _ => Answer(HttpStatusCode.RequestEntityTooLarge, """{"code":"payload_too_large"}""");

        using var json = await (await _factory.Page().ProbeAsync(TenGiB, "declared", expectContinue: true)).ReadJsonAsync();

        Assert.Equal("refused", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(413, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("bytesRead").GetInt64());
        var sent = Assert.Single(_factory.Cms.Requests);
        Assert.Equal(TenGiB, sent.ContentLength);
        Assert.True(sent.ExpectContinue);
        Assert.Equal(ConsoleFactory.Basic(ConsoleFactory.CmsUser, ConsoleFactory.CmsPassword), sent.Authorization);
        Assert.Equal("http://cms.test/cms/events", sent.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task A_chunked_probe_declares_no_length()
    {
        _factory.Cms.Respond = _ => Answer(HttpStatusCode.RequestEntityTooLarge, "{}");

        await _factory.Page().ProbeAsync(TenGiB, "chunked");

        Assert.Null(Assert.Single(_factory.Cms.Requests).ContentLength);
    }

    [Fact]
    public async Task A_body_the_cms_accepts_is_read_in_full()
    {
        _factory.Cms.Respond = _ => Answer(HttpStatusCode.BadRequest, """{"code":"invalid_json"}""");

        using var json = await (await _factory.Page().ProbeAsync(1024)).ReadJsonAsync();

        Assert.Equal("completed", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1024, json.RootElement.GetProperty("bytesRead").GetInt64());
        Assert.Equal(new string(' ', 1024), Assert.Single(_factory.Cms.Requests).Body);
    }

    [Fact]
    public async Task A_dropped_connection_is_reported_as_one()
    {
        _factory.Cms.Throw = new HttpRequestException("send failed", new IOException("connection forcibly closed"));

        using var json = await (await _factory.Page().ProbeAsync(TenGiB)).ReadJsonAsync();

        Assert.Equal("connection_reset", json.RootElement.GetProperty("outcome").GetString());
        Assert.Contains("forcibly closed", json.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(0, "declared")]
    [InlineData(-5, "declared")]
    [InlineData(17L * 1024 * 1024 * 1024, "declared")]
    [InlineData(1024, "sideways")]
    public async Task Silly_probes_are_refused(long bytes, string mode)
    {
        var response = await _factory.Page().ProbeAsync(bytes, mode);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    // ------------------------------------------------------------ the page itself

    [Theory]
    [InlineData("/")]
    [InlineData("/engineering")]
    public async Task Each_page_is_served_with_a_strict_content_security_policy(string page)
    {
        var response = await _factory.CreateClient().GetAsync(page);

        var policy = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("default-src 'none'", policy);
        Assert.DoesNotContain("unsafe", policy);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/engineering")]
    public async Task The_html_has_no_inline_script_or_style_that_the_policy_would_block(string page)
    {
        var html = await _factory.CreateClient().GetStringAsync(page);

        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", html);
        Assert.DoesNotMatch(@"\sstyle\s*=", html);
        Assert.DoesNotMatch(@"\son\w+\s*=", html);
    }

    [Theory]
    [InlineData("innerHTML")]
    [InlineData("outerHTML")]
    [InlineData("insertAdjacentHTML")]
    [InlineData("document.write")]
    [InlineData("eval(")]
    [InlineData("new Function")]
    public async Task The_script_never_turns_text_from_the_cms_into_markup(string forbidden)
    {
        // Every script either page loads: the engineering console's and the photo library's.
        foreach (var name in AllScripts)
        {
            var script = await _factory.CreateClient().GetStringAsync("/" + name);

            Assert.False(script.Contains(forbidden), $"{name} contains {forbidden}");
        }
    }

    // Every script the two pages load.
    private static readonly string[] AllScripts =
    [
        "eng-core.js", "eng-overview.js", "eng-design.js", "eng-requirements.js", "eng-records.js", "eng-events.js", "eng-assets.js", "eng-workbench.js",
        "eng-ops.js", "eng-verify.js", "eng-start.js", "photos-lib.js", "photos-store.js", "photos-app.js",
    ];

    [Fact]
    public async Task Every_script_a_page_loads_exists_and_every_script_that_exists_is_loaded()
    {
        var client = _factory.CreateClient();
        var pages = await client.GetStringAsync("/") + await client.GetStringAsync("/engineering");

        foreach (var name in AllScripts)
        {
            Assert.Contains($"src=\"{name}\"", pages);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/" + name)).StatusCode);
        }
    }

    [Fact]
    public async Task A_diagram_opened_on_its_own_may_style_itself_but_nothing_else()
    {
        var response = await _factory.CreateClient().GetAsync("/diagrams/architecture.svg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        var policy = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("style-src 'unsafe-inline'", policy);
        Assert.Contains("default-src 'none'", policy);
        Assert.DoesNotContain("script-src", policy);
    }

    [Fact]
    public async Task The_endpoint_catalogue_and_every_diagram_data_file_are_valid_json_the_console_serves()
    {
        var client = _factory.CreateClient();

        foreach (var path in new[] { "/endpoints.json", "/requirements.json", "/brief-trace.json", "/diagrams/architecture.json", "/diagrams/event-lifecycle.json", "/diagrams/asset-lifecycle.json" })
        {
            var text = await client.GetStringAsync(path);
            using var document = System.Text.Json.JsonDocument.Parse(text);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    [Fact]
    public async Task The_photo_library_is_the_front_page_and_the_engineering_console_is_one_click_away()
    {
        var client = _factory.CreateClient();

        var photos = await client.GetStringAsync("/");
        var engineering = await client.GetStringAsync("/engineering");

        Assert.Contains("<title>Lateral Photos</title>", photos);
        Assert.Contains("href=\"/engineering\"", photos);
        Assert.Contains("<title>Engineering", engineering);
        Assert.Contains("Testing the CMS at", engineering);
        Assert.Contains("href=\"/\"", engineering);
    }

    [Fact]
    public async Task Scripts_and_styles_are_always_revalidated_so_an_edit_shows_at_once()
    {
        var response = await _factory.CreateClient().GetAsync("/photos-app.js");

        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string json) => FakeCms.Json(status, json);
}
