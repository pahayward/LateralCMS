using Lateral.Cms.Auth;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Tests.Auth;

/// <summary>Credential rules from the brief, enforced at startup and in the store.</summary>
[Trait("req", "IN-02")]
[Trait("req", "SE-06")]
public class AuthOptionsTests
{
    private static readonly string CmsGuid = Guid.NewGuid().ToString();

    private static AuthOptions Valid() => new()
    {
        Cms = new CmsAccountOptions { Username = "cms-ingest-svc", Passwords = [CmsGuid] },
        Users =
        [
            new UserAccountOptions { Username = "user@x.example", Password = "a-long-enough-password", Role = "User" },
            new UserAccountOptions { Username = "admin@x.example", Password = "another-long-password", Role = "Admin" },
        ],
    };

    private static ValidateOptionsResult Validate(AuthOptions options) => new AuthOptionsValidator().Validate(null, options);

    [Fact]
    public void A_complete_configuration_is_valid()
    {
        Assert.True(Validate(Valid()).Succeeded);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("nine-char")] // 9
    [InlineData("this-username-is-way-too-long")]
    [InlineData("")]
    public void The_cms_username_must_be_10_to_20_characters(string username)
    {
        var options = Valid();
        options.Cms.Username = username;

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains("10 to 20", result.FailureMessage);
    }

    [Theory]
    [InlineData("ten-chars!")] // 10
    [InlineData("exactly-twenty-chars")] // 20
    public void The_cms_username_length_boundaries_are_inclusive(string username)
    {
        var options = Valid();
        options.Cms.Username = username;

        Assert.True(Validate(options).Succeeded);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void The_cms_password_must_be_a_real_guid(string password)
    {
        var options = Valid();
        options.Cms.Passwords = [password];

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void At_least_one_cms_password_is_required()
    {
        var options = Valid();
        options.Cms.Passwords = [];

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void Every_listed_cms_password_must_be_a_guid()
    {
        var options = Valid();
        options.Cms.Passwords = [CmsGuid, "oops"];

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void Usernames_must_be_unique_including_the_cms_account()
    {
        var options = Valid();
        options.Users[1].Username = "USER@x.example";
        Assert.True(Validate(options).Failed);

        options = Valid();
        options.Users[0].Username = "cms-ingest-svc";
        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void Usernames_cannot_contain_a_colon_which_basic_auth_cannot_carry()
    {
        var options = Valid();
        options.Users[0].Username = "a:b@x.example";

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void User_passwords_must_be_long_enough_and_roles_known()
    {
        var weak = Valid();
        weak.Users[0].Password = "short";
        Assert.True(Validate(weak).Failed);

        var unknownRole = Valid();
        unknownRole.Users[0].Role = "SuperUser";
        Assert.True(Validate(unknownRole).Failed);
    }

    [Fact]
    public void A_missing_configuration_fails_rather_than_serving_with_defaults()
    {
        var result = Validate(new AuthOptions());

        Assert.True(result.Failed);
    }

    // ---- credential store -----------------------------------------------------------------------------

    private static CredentialStore Store() => new(Options.Create(Valid()));

    [Fact]
    public void The_store_maps_each_account_to_its_role()
    {
        Assert.True(Store().Validate("cms-ingest-svc", CmsGuid)!.IsInRole(Roles.CmsIngest));
        Assert.True(Store().Validate("user@x.example", "a-long-enough-password")!.IsInRole(Roles.User));
        Assert.True(Store().Validate("admin@x.example", "another-long-password")!.IsInRole(Roles.Admin));
    }

    [Theory]
    [InlineData("cms-ingest-svc", "nope")]
    [InlineData("nobody", "a-long-enough-password")]
    [InlineData("user@x.example", "another-long-password")]
    [InlineData("USER@X.EXAMPLE", "a-long-enough-password")]
    [InlineData("", "")]
    public void The_store_rejects_everything_else(string username, string password)
    {
        Assert.Null(Store().Validate(username, password));
    }
}
