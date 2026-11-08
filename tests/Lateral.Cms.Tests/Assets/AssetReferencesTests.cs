using Lateral.Cms.Assets;

namespace Lateral.Cms.Tests.Assets;

public class AssetReferencesTests
{
    private static readonly Guid A = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid B = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("{\"a\":")]
    public void Nothing_usable_names_nothing(string? payload) => Assert.Empty(AssetReferences.In(payload));

    [Fact]
    public void Finds_guids_at_any_depth_in_objects_and_arrays()
    {
        var found = AssetReferences.In($$$"""{"a":"{{{A}}}","b":{"c":["x",{"d":"{{{B}}}"}]}}""");
        Assert.Equal(new HashSet<Guid> { A, B }, found);
    }

    [Fact]
    public void Matches_regardless_of_letter_case_and_counts_a_repeat_once()
    {
        var found = AssetReferences.In($$"""{"a":"{{A.ToString().ToUpperInvariant()}}","b":"{{A}}"}""");
        Assert.Equal([A], found);
    }

    [Theory]
    [InlineData("11111111222233334444555555555555")]      // no hyphens
    [InlineData("{11111111-2222-3333-4444-555555555555}")] // braces
    [InlineData(" 11111111-2222-3333-4444-555555555555")]  // padded
    public void Only_the_plain_hyphenated_form_counts(string text) =>
        Assert.Empty(AssetReferences.In($$"""{"a":"{{text}}"}"""));

    [Fact]
    public void Numbers_and_property_names_are_not_values()
    {
        Assert.Empty(AssetReferences.In($$"""{"{{A}}":1,"n":12345678}"""));
    }

    [Fact]
    public void A_guid_written_with_json_escapes_is_still_found()
    {
        var escaped = A.ToString().Replace("-", "\\u002d");
        Assert.Equal([A], AssetReferences.In($$"""{"a":"{{escaped}}"}"""));
    }
}
