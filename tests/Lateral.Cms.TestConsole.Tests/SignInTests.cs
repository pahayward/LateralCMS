using System.Net;
using System.Net.Http.Json;

namespace Lateral.Cms.TestConsole.Tests;

public sealed class SignInFactory : ConsoleFactory
{
    protected override bool SignInEnabled => true;
}

/// <summary>The sign-in in front of the console: nothing opens without it, and the photo-library person gets the photo library only.</summary>
public class SignInTests : IClassFixture<SignInFactory>
{
    private readonly SignInFactory _factory;

    public SignInTests(SignInFactory factory)
    {
        _factory = factory;
        _factory.Cms.Reset();
    }

    private HttpClient Browser() => _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<HttpResponseMessage> LogIn(HttpClient client, string name, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/login") { Content = JsonContent.Create(new { username = name, password }) };
        request.Headers.Add("X-Console-Request", "1");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string path, string? actAs = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Add("X-Console-Request", "1");
        if (actAs is not null)
        {
            request.Headers.Add("X-Console-Act-As", actAs);
        }

        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/engineering")]
    [InlineData("/eng-core.js")]
    [InlineData("/requirements.json")]
    public async Task A_page_or_file_without_a_session_goes_to_the_sign_in_page(string path)
    {
        var response = await Browser().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/api/config")]
    [InlineData("/api/cms/api/entities")]
    [InlineData("/media/6f1c7a02-0000-4000-8000-000000000000")]
    public async Task An_api_call_without_a_session_is_401_and_nothing_reaches_the_cms(string path)
    {
        var response = await Get(Browser(), path, "admin");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    [Fact]
    public async Task The_sign_in_page_itself_is_open()
    {
        var response = await Browser().GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign in", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("admin", "wrong")]
    [InlineData("nobody", "admin")]
    [InlineData("admin", "")]
    public async Task A_wrong_name_or_password_is_refused_and_sets_no_cookie(string name, string password)
    {
        var response = await LogIn(Browser(), name, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task The_admin_signs_in_gets_a_hardened_cookie_and_opens_everything()
    {
        var client = Browser();
        var login = await LogIn(client, "admin", "admin");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", cookie.Split(';')[0].Split('=')[1].Split('.')[1], StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/engineering")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/api/config")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/api/cms/health", "anonymous")).StatusCode);
    }

    [Fact]
    public async Task The_photo_library_person_opens_the_library_but_not_the_engineering_console()
    {
        var client = Browser();
        Assert.Equal(HttpStatusCode.OK, (await LogIn(client, "user", "password")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/photos-app.js")).StatusCode);

        var engineering = await client.GetAsync("/engineering");
        Assert.Equal(HttpStatusCode.Redirect, engineering.StatusCode);
        Assert.Equal("/", engineering.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/eng-core.js")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/requirements.json")).StatusCode);
    }

    [Theory]
    [InlineData("/api/verify/abc", "GET", "admin")]
    [InlineData("/api/probe/oversize", "POST", "admin")]
    [InlineData("/api/cms/api/admin/ingestion", "GET", "admin")]
    [InlineData("/api/cms/health", "GET", "anonymous")]
    [InlineData("/api/cms/api/entities", "GET", "wrong")]
    [InlineData("/api/cms/api/entities/x/disable", "POST", "admin")]
    public async Task The_photo_library_person_cannot_use_the_consoles_other_tools_or_borrow_other_identities(string path, string method, string actAs)
    {
        var client = Browser();
        await LogIn(client, "user", "password");

        var response = await Get(client, path, actAs, new HttpMethod(method));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Cms.Requests);
    }

    [Fact]
    public async Task The_photo_library_person_can_make_the_calls_the_library_makes()
    {
        var client = Browser();
        await LogIn(client, "user", "password");

        var response = await Get(client, "/api/cms/api/entities?limit=100", "user");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_factory.Cms.Requests);
    }

    [Fact]
    public async Task A_forged_or_altered_cookie_is_not_a_session()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/engineering");
        request.Headers.Add("Cookie", "lateral_console=" + Convert.ToBase64String("admin|admin|9999999999"u8.ToArray()) + ".00");

        Assert.Equal(HttpStatusCode.Redirect, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var client = Browser();
        await LogIn(client, "admin", "admin");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/logout");
        request.Headers.Add("X-Console-Request", "1");
        await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/engineering")).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_name_out_for_a_while()
    {
        var client = Browser();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LogIn(client, "locked-out-name", "nope")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LogIn(client, "locked-out-name", "nope")).StatusCode);
    }
}
