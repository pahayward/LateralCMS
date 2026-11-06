using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lateral.Cms.Tests.Domain;

namespace Lateral.Cms.Tests.Delivery;

/// <summary>
/// Keeps the documentation honest: the requirements matrix lists the tests that really exist, every MUST requirement has
/// one, every deliberate defect named in it was caught, and the relative links in the docs resolve. Regenerate the Tests
/// column with <c>UPDATE_REQUIREMENTS=1 dotnet test --filter DeliveryTests</c>.
/// </summary>
public partial class DeliveryTests
{
    private static readonly string Root = DomainArchitectureTests.RepositoryRoot();

    private static string RequirementsPath => Path.Combine(Root, "docs", "REQUIREMENTS.md");

    private sealed record Row(int Line, string Id, string Priority, string Evidence, string Mutations, string Tests);

    [GeneratedRegex(@"^\| [A-Z]{2}-\d{2} \|")]
    private static partial Regex RowStart();

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    private static (string[] Lines, List<Row> Rows) ReadRequirements()
    {
        var lines = File.ReadAllLines(RequirementsPath);
        var rows = new List<Row>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!RowStart().IsMatch(lines[i]))
            {
                continue;
            }

            // ID | Requirement | Pri | Evidence | Limit | Status | Mutations | Tests
            var cells = lines[i].Split('|').Skip(1).SkipLast(1).Select(c => c.Trim()).ToArray();
            Assert.True(cells.Length == 8, $"REQUIREMENTS.md line {i + 1} has {cells.Length} cells, expected 8");
            rows.Add(new Row(i, cells[0], cells[2], cells[3], cells[6], cells[7]));
        }

        return (lines, rows);
    }

    private static IEnumerable<string> RequirementIds(IEnumerable<CustomAttributeData> attributes) =>
        attributes
            .Where(a => a.AttributeType.Name == "TraitAttribute" && (string?)a.ConstructorArguments[0].Value == "req")
            .Select(a => (string)a.ConstructorArguments[1].Value!);

    private static SortedDictionary<string, SortedDictionary<string, int>> TestsByRequirement()
    {
        var map = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var type in typeof(DeliveryTests).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            var classIds = RequirementIds(type.CustomAttributes).ToList();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!method.CustomAttributes.Any(a => a.AttributeType.Name is "FactAttribute" or "TheoryAttribute"))
                {
                    continue;
                }

                foreach (var id in classIds.Concat(RequirementIds(method.CustomAttributes)).Distinct())
                {
                    if (!map.TryGetValue(id, out var classes))
                    {
                        map[id] = classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    }

                    classes[type.Name] = classes.GetValueOrDefault(type.Name) + 1;
                }
            }
        }

        return map;
    }

    private static string Describe(SortedDictionary<string, int>? classes) =>
        classes is null ? "-" : string.Join(", ", classes.Select(c => $"`{c.Key}` ({c.Value})"));

    /// <summary>Evidence codes that mean "an automated test is expected".</summary>
    private static bool NeedsTests(string evidence) => evidence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(c => c is "U" or "P" or "I" or "F" or "C" or "Q" or "A" or "L" or "V");

    [Fact]
    [Trait("req", "TE-05")]
    public void The_tests_column_of_the_requirements_matrix_lists_the_tests_that_exist()
    {
        var (lines, rows) = ReadRequirements();
        var tests = TestsByRequirement();

        var stale = new List<string>();
        foreach (var row in rows)
        {
            var expected = Describe(tests.GetValueOrDefault(row.Id));
            if (expected != row.Tests)
            {
                stale.Add($"{row.Id}: expected {expected}, found {row.Tests}");
                var cells = lines[row.Line].Split('|').Skip(1).SkipLast(1).Select(c => c.Trim()).ToArray();
                cells[7] = expected;
                lines[row.Line] = "| " + string.Join(" | ", cells) + " |";
            }
        }

        if (stale.Count > 0 && Environment.GetEnvironmentVariable("UPDATE_REQUIREMENTS") == "1")
        {
            File.WriteAllLines(RequirementsPath, lines);
            return;
        }

        Assert.True(stale.Count == 0, "docs/REQUIREMENTS.md is stale; run UPDATE_REQUIREMENTS=1 dotnet test --filter DeliveryTests\n" + string.Join('\n', stale.Take(10)));
    }

    [Fact]
    [Trait("req", "TE-05")]
    public void Every_must_requirement_that_needs_automated_tests_has_at_least_one()
    {
        var (_, rows) = ReadRequirements();
        var tests = TestsByRequirement();

        var missing = rows
            .Where(r => r.Priority.StartsWith("MUST", StringComparison.Ordinal) && NeedsTests(r.Evidence) && !tests.ContainsKey(r.Id))
            .Select(r => r.Id)
            .ToList();

        Assert.True(missing.Count == 0, "MUST requirements with no test naming them: " + string.Join(", ", missing));
    }

    [Fact]
    [Trait("req", "TE-05")]
    public void Every_requirement_id_a_test_names_exists_in_the_matrix()
    {
        var (_, rows) = ReadRequirements();
        var known = rows.Select(r => r.Id).ToHashSet();

        var unknown = TestsByRequirement().Keys.Where(id => !known.Contains(id)).ToList();

        Assert.True(unknown.Count == 0, "tests name requirements that are not in docs/REQUIREMENTS.md: " + string.Join(", ", unknown));
    }

    private sealed record MutationResult(string Id, string Outcome, string[] Failed);

    private static Dictionary<string, MutationResult> LastMutationRun()
    {
        var path = Path.Combine(Root, "tools", "mutation-check", "last-run.json");
        if (!File.Exists(path))
        {
            return [];
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateArray()
            .Select(e => new MutationResult(
                e.GetProperty("id").GetString()!, e.GetProperty("outcome").GetString()!,
                [.. e.GetProperty("failed").EnumerateArray().Select(f => f.GetString()!)]))
            .ToDictionary(r => r.Id);
    }

    [Fact]
    [Trait("req", "TE-06")]
    public void Every_requirement_that_needs_a_mutation_names_defects_that_a_test_caught()
    {
        var (_, rows) = ReadRequirements();
        var defined = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tools", "mutation-check", "mutations.json")))
            .RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToHashSet();
        var results = LastMutationRun();

        var problems = new List<string>();
        foreach (var row in rows.Where(r => r.Evidence.Split(' ').Contains("M") && r.Id != "TE-06"))
        {
            var ids = row.Mutations == "-" ? [] : row.Mutations.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0)
            {
                problems.Add($"{row.Id} needs a mutation but names none");
            }

            foreach (var id in ids)
            {
                if (!defined.Contains(id))
                {
                    problems.Add($"{row.Id} names {id}, which is not in mutations.json");
                }
                else if (!results.TryGetValue(id, out var result) || result.Outcome != "killed")
                {
                    problems.Add($"{row.Id}: {id} was not caught in the last recorded run ({(results.TryGetValue(id, out result) ? result.Outcome : "never run")})");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    [Trait("req", "TE-06")]
    public void The_recorded_mutation_run_has_at_least_25_defects_and_every_one_was_caught_by_a_named_test()
    {
        var results = LastMutationRun();

        Assert.True(results.Count >= 25, $"only {results.Count} mutations recorded");
        Assert.All(results.Values, r => Assert.True(r.Outcome == "killed" && r.Failed.Length > 0, $"{r.Id}: {r.Outcome}"));
    }

    [Fact]
    [Trait("req", "DL-06")]
    public void Relative_links_in_the_readme_and_the_docs_resolve()
    {
        var files = new[] { Path.Combine(Root, "README.md") }
            .Concat(Directory.GetFiles(Path.Combine(Root, "docs"), "*.md").Where(f => !f.EndsWith("REBUILD-BRIEF.md", StringComparison.Ordinal)))
            .ToList();

        var broken = new List<string>();
        foreach (var file in files)
        {
            var text = Regex.Replace(File.ReadAllText(file), @"```.*?```", "", RegexOptions.Singleline);
            foreach (Match match in MarkdownLink().Matches(text))
            {
                var target = match.Groups[1].Value;
                if (target.StartsWith('#') || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal))
                {
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(target.Split('#')[0])));
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    broken.Add($"{Path.GetRelativePath(Root, file)} -> {target}");
                }
            }
        }

        Assert.True(broken.Count == 0, "broken links:\n" + string.Join('\n', broken));
    }

    [Fact]
    [Trait("req", "DL-01")]
    public void The_projects_target_net9_and_roll_forward_so_a_newer_runtime_can_run_them()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));

        Assert.Contains("<TargetFramework>net9.0</TargetFramework>", props);
        Assert.Contains("<RollForward>Major</RollForward>", props);
        Assert.Contains("<Nullable>enable</Nullable>", props);
    }

    [Fact]
    [Trait("req", "SE-06")]
    public void No_credentials_are_in_the_settings_file_that_is_committed_for_every_environment()
    {
        var settings = File.ReadAllText(Path.Combine(Root, "src", "Lateral.Cms", "appsettings.json"));
        using var document = JsonDocument.Parse(settings);

        Assert.False(document.RootElement.TryGetProperty("Auth", out var auth) && (auth.TryGetProperty("Cms", out _) || auth.TryGetProperty("Users", out _)));
        Assert.DoesNotContain("Passwords", settings);

        // And the development file, the one place credentials may live, is not served in any other environment.
        var gitignore = File.ReadAllText(Path.Combine(Root, ".gitignore"));
        Assert.Contains("appsettings.Local.json", gitignore);
    }
}
