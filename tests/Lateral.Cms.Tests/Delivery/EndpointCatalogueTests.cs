using System.Net;
using System.Text.Json;
using Lateral.Cms.Tests.Auth;
using Lateral.Cms.Tests.Domain;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Delivery;

/// <summary>
/// The engineering console's catalogue of endpoints (endpoints.json) drives its API workbench, its curl generator and the
/// strip of endpoints under each page title. It is data, so a route added, removed or opened to another role would leave
/// the console quietly describing a service that no longer exists. These tests keep it true.
/// </summary>
[Trait("req", "DX-01")]
public class EndpointCatalogueTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private static readonly string[] Views = ["overview", "records", "events", "assets", "ops", "verify", "workbench", "design"];

    private sealed record Entry(string Id, string Route, string Area, string[] Who, string View, bool Optional, string[] Statuses, bool HasWhat);

    private static List<Entry> Catalogue()
    {
        var path = Path.Combine(DomainArchitectureTests.RepositoryRoot(), "tools", "Lateral.Cms.TestConsole", "wwwroot", "endpoints.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("endpoints").EnumerateArray().Select(e => new Entry(
            e.GetProperty("id").GetString()!,
            e.GetProperty("route").GetString()!,
            e.GetProperty("area").GetString()!,
            e.GetProperty("who").EnumerateArray().Select(w => w.GetString()!).ToArray(),
            e.GetProperty("view").GetString()!,
            e.TryGetProperty("optional", out var o) && o.GetBoolean(),
            e.GetProperty("responses").EnumerateObject().Select(r => r.Name).ToArray(),
            e.TryGetProperty("what", out var w2) && w2.GetString()!.Length > 0)).ToList();
    }

    private HashSet<string> Mapped() => factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
        .OfType<RouteEndpoint>()
        .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} /{e.RoutePattern.RawText!.Trim('/')}"))
        .ToHashSet();

    [Fact]
    public void The_catalogue_lists_every_route_the_service_maps_and_no_route_it_does_not()
    {
        var mapped = Mapped();
        var listed = Catalogue();

        Assert.Empty(mapped.Except(listed.Select(e => e.Route)));
        // The reset exists only when switched on, which the default test configuration does not do.
        Assert.Empty(listed.Where(e => !e.Optional).Select(e => e.Route).Except(mapped));
        Assert.Equal(listed.Count, listed.Select(e => e.Route).Distinct().Count());
        Assert.Equal(listed.Count, listed.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void The_catalogue_names_the_same_callers_as_the_role_matrix_proves()
    {
        var listed = Catalogue().ToDictionary(e => e.Route);

        foreach (var row in RoleMatrixTests.Matrix())
        {
            var endpoint = (string)row[0];
            var caller = (string)row[1];
            var status = (HttpStatusCode)row[2];
            var allowed = status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

            Assert.True(listed.TryGetValue(endpoint, out var entry), $"{endpoint} is not in the catalogue");
            Assert.True(entry!.Who.Contains(caller) == allowed,
                $"{endpoint} as {caller}: the matrix says {(allowed ? "allowed" : "refused")}, the catalogue says {string.Join(",", entry.Who)}");
        }
    }

    [Theory]
    [InlineData("architecture")]
    [InlineData("event-lifecycle")]
    [InlineData("asset-lifecycle")]
    public void A_diagram_describes_real_files_and_real_tests_and_every_walkthrough_step_points_at_a_real_box(string name)
    {
        var root = DomainArchitectureTests.RepositoryRoot();
        var path = Path.Combine(root, "tools", "Lateral.Cms.TestConsole", "wwwroot", "diagrams", name + ".json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var top = document.RootElement;
        double width = top.GetProperty("width").GetDouble();
        double height = top.GetProperty("height").GetDouble();
        var nodes = top.GetProperty("nodes").EnumerateArray().ToList();
        var ids = nodes.Select(n => n.GetProperty("id").GetString()!).ToList();

        // the test classes that exist, found the cheap way: their declarations
        var declared = Directory.GetFiles(Path.Combine(root, "tests", "Lateral.Cms.Tests"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"class (\w+)").Select(m => m.Groups[1].Value))
            .ToHashSet();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var node in nodes)
        {
            var id = node.GetProperty("id").GetString();
            Assert.True(node.GetProperty("x").GetDouble() >= 0 && node.GetProperty("y").GetDouble() >= 0, $"{id} starts outside the picture");
            Assert.True(node.GetProperty("x").GetDouble() + node.GetProperty("w").GetDouble() <= width, $"{id} runs off the right of the picture");
            Assert.True(node.GetProperty("y").GetDouble() + node.GetProperty("h").GetDouble() <= height, $"{id} runs off the bottom of the picture");

            foreach (var code in node.GetProperty("code").EnumerateArray())
            {
                Assert.True(File.Exists(Path.Combine(root, code.GetString()!)), $"{id} names a file that does not exist: {code}");
            }

            foreach (var test in node.GetProperty("tests").EnumerateArray())
            {
                Assert.True(declared.Contains(test.GetString()!), $"{id} names a test class that does not exist: {test}");
            }
        }

        foreach (var walk in top.GetProperty("walks").EnumerateArray())
        {
            foreach (var step in walk.GetProperty("steps").EnumerateArray())
            {
                foreach (var id in step.GetProperty("nodes").EnumerateArray())
                {
                    Assert.Contains(id.GetString(), ids);
                }
            }
        }
    }

    [Fact]
    public void Every_entry_explains_the_responses_a_caller_will_actually_meet()
    {
        foreach (var entry in Catalogue())
        {
            Assert.True(entry.HasWhat, $"{entry.Route} has no description");
            Assert.Contains(entry.View, Views);
            Assert.NotEmpty(entry.Statuses);

            // Anything behind credentials must say what 401 and 403 mean (health needs none).
            if (!entry.Who.Contains("anonymous"))
            {
                Assert.Contains("401", entry.Statuses);
                Assert.Contains("403", entry.Statuses);
            }
        }
    }
}
