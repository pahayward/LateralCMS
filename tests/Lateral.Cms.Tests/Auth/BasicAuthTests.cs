using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Auth;

/// <summary>
/// Basic authentication, end to end: valid and invalid username/password combinations, and the rule that
/// the CMS account and the user accounts can each only do their own job.
/// </summary>
[Trait("req", "SE-04")]
[Trait("req", "IN-01")]
[Trait("req", "SE-03")]
[Trait("req", "SE-02")]
public class BasicAuthTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private const string Ingest = "/cms/events";
    private const string Api = "/api/entities";

    private static StringContent EmptyBatch => Evt.Raw("[]");

    private HttpClient Anonymous() => factory.CreateClient();

    private async Task<HttpStatusCode> PostIngest(HttpClient client) => (await client.PostAsync(Ingest, EmptyBatch)).StatusCode;

    private async Task<HttpStatusCode> GetApi(HttpClient client) => (await client.GetAsync(Api)).StatusCode;

    // ---- the CMS endpoint -------------------------------------------------------------------------------

    [Fact]
    public async Task Valid_cms_credentials_are_accepted()
    {
        Assert.Equal(HttpStatusCode.OK, await PostIngest(factory.Cms()));
    }

    [Fact]
    public async Task Both_cms_passwords_work_while_a_rotation_is_in_progress()
    {
        Assert.Equal(HttpStatusCode.OK, await PostIngest(factory.ClientFor(CmsFactory.CmsUser, CmsFactory.CmsPassword)));
        Assert.Equal(HttpStatusCode.OK, await PostIngest(factory.ClientFor(CmsFactory.CmsUser, CmsFactory.CmsRotatedPassword)));
    }

    [Fact]
    public async Task No_credentials_gets_401_with_a_basic_challenge()
    {
        var response = await Anonymous().PostAsync(Ingest, EmptyBatch);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Basic", challenge.Scheme);
    }

    [Theory]
    [InlineData(CmsFactory.CmsUser, "wrong-password")]
    [InlineData("someone-else-entirely", "00000000-0000-0000-0000-000000000001")]
    [InlineData(CmsFactory.CmsUser, "")]
    [InlineData("", "")]
    [InlineData("CMS-INGEST-SVC", "ignored")]
    public async Task Invalid_cms_username_password_combinations_are_rejected(string username, string password)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await PostIngest(factory.ClientFor(username, password)));
    }

    [Fact]
    public async Task The_right_username_with_the_password_of_another_account_is_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await PostIngest(factory.ClientFor(CmsFactory.CmsUser, CmsFactory.UserPassword)));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostIngest(factory.ClientFor(CmsFactory.UserName, CmsFactory.CmsPassword)));
    }

    [Fact]
    public async Task The_password_is_case_sensitive_and_whitespace_sensitive()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await PostIngest(factory.ClientFor(CmsFactory.CmsUser, CmsFactory.CmsPassword.ToUpperInvariant())));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostIngest(factory.ClientFor(CmsFactory.CmsUser, CmsFactory.CmsPassword + " ")));
    }

    [Theory]
    [InlineData("Basic", "!!!not-base64!!!")]
    [InlineData("Basic", "anNvbm5vY29sb24=")] // "jsonnocolon": decodes, but has no user:password separator
    [InlineData("Basic", "")]
    [InlineData("Bearer", "abc.def.ghi")]
    public async Task Malformed_or_foreign_authorization_headers_are_rejected(string scheme, string parameter)
    {
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, parameter);

        Assert.Equal(HttpStatusCode.Unauthorized, await PostIngest(client));
    }

    [Fact]
    public async Task The_scheme_name_is_case_insensitive()
    {
        var client = Anonymous();
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CmsFactory.CmsUser}:{CmsFactory.CmsPassword}"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"basic {token}");

        Assert.Equal(HttpStatusCode.OK, await PostIngest(client));
    }

    [Fact]
    public async Task A_password_may_itself_contain_a_colon()
    {
        // Only the first colon separates user from password; a wrong password containing ':' is simply wrong.
        Assert.Equal(HttpStatusCode.Unauthorized, await PostIngest(factory.ClientFor(CmsFactory.CmsUser, "a:b:c")));
    }

    // ---- the user API -----------------------------------------------------------------------------------

    [Fact]
    public async Task Valid_user_and_admin_credentials_can_read()
    {
        Assert.Equal(HttpStatusCode.OK, await GetApi(factory.User()));
        Assert.Equal(HttpStatusCode.OK, await GetApi(factory.Admin()));
    }

    [Fact]
    public async Task The_api_requires_credentials()
    {
        var response = await Anonymous().GetAsync(Api);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEmpty(response.Headers.WwwAuthenticate);
    }

    [Theory]
    [InlineData(CmsFactory.UserName, "wrong-password-123")]
    [InlineData("nobody@test.example", "whatever-password")]
    [InlineData(CmsFactory.AdminName, "")]
    public async Task Invalid_user_username_password_combinations_are_rejected(string username, string password)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await GetApi(factory.ClientFor(username, password)));
    }

    [Fact]
    public async Task Individual_entities_are_protected_too()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync($"{Api}/anything")).StatusCode);
    }

    // ---- roles: each account can only do its own job ---------------------------------------------------

    [Fact]
    public async Task The_cms_account_cannot_read_the_api()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await GetApi(factory.Cms()));
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Cms().GetAsync($"{Api}/anything")).StatusCode);
    }

    [Fact]
    public async Task Users_and_admins_cannot_post_cms_events()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await PostIngest(factory.User()));
        Assert.Equal(HttpStatusCode.Forbidden, await PostIngest(factory.Admin()));
    }

    [Fact]
    public async Task Only_admins_can_disable_or_enable_entities()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.User().PostAsync($"{Api}/x/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Cms().PostAsync($"{Api}/x/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.User().PostAsync($"{Api}/x/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsync($"{Api}/x/disable", null)).StatusCode);
    }

    // ---- what is open ----------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "AP-07")]
    public async Task Health_is_the_only_anonymous_endpoint_and_reveals_nothing_but_status()
    {
        var response = await Anonymous().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait("req", "AP-08")]
    public async Task Responses_are_marked_uncacheable_and_unsniffable()
    {
        var response = await factory.User().GetAsync(Api);

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
    }
}
