using System.Net;
using System.Text;
using Lateral.Cms.Tests.Assets;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Api;

/// <summary>
/// The service run as "Development". That environment makes ASP.NET throw when a query value or body cannot be read, which
/// without a fix is a 500; every other environment answers 400. Credentials are supplied, so none are generated.
/// </summary>
public sealed class DevelopmentAssetFactory : AssetFactory
{
    protected override string EnvironmentName => "Development";
}

[Trait("req", "SH-08")]
public class BadInputTests(DevelopmentAssetFactory factory) : IClassFixture<DevelopmentAssetFactory>
{
    [Theory]
    [InlineData("limit=abc")]
    [InlineData("limit=1.5")]
    [InlineData("limit=")]
    [InlineData("limit=2&limit=3")]
    [InlineData("limit=99999999999999999999")]
    public async Task A_query_value_that_is_not_a_number_is_a_400_even_in_development(string query)
    {
        var response = await factory.User().GetAsync($"/api/entities?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_unknown_asset_state_in_the_admin_list_is_a_400()
    {
        var response = await factory.Admin().GetAsync("/api/admin/assets?state=banana");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public async Task Starting_an_upload_with_a_body_that_is_not_the_expected_json_is_a_400(string body)
    {
        var response = await factory.Cms().PostAsync("/cms/assets/uploads", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_bad_range_unit_is_ignored_and_the_whole_file_is_sent()
    {
        var id = await factory.CleanAssetAsync(AssetFactory.Png(600, 91), "range-unit.png", "image/png");
        var user = factory.User();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{id}");
        request.Headers.TryAddWithoutValidation("Range", "items=0-1");
        var odd = await user.SendAsync(request);
        using var proper = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{id}");
        proper.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
        var bytes = await user.SendAsync(proper);

        Assert.Equal(HttpStatusCode.OK, odd.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, bytes.StatusCode);
        Assert.True((await odd.Content.ReadAsByteArrayAsync()).Length > 2);
    }
}
