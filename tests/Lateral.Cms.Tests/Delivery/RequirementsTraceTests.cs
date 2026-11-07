using System.Text.Json;
using System.Text.RegularExpressions;
using Lateral.Cms.Tests.Domain;

namespace Lateral.Cms.Tests.Delivery;

/// <summary>
/// The engineering console's Requirements page puts each original requirement beside how it was met. Its answers are written by
/// hand (brief-trace.json) and its rows come from docs/REQUIREMENTS.md (requirements.json, generated). These tests stop it
/// saying more than is true: the brief's words must be the brief's words, "met" may cite only requirements that are Done,
/// anything partial or open must say what is missing, and the generated data must match the document.
/// </summary>
[Trait("req", "DX-02")]
public partial class RequirementsTraceTests
{
    private static readonly string Root = DomainArchitectureTests.RepositoryRoot();
    private static readonly string[] Views = ["overview", "records", "events", "assets", "ops", "verify", "workbench", "design", "requirements", "accounts", "settings"];

    private sealed record Row(string Id, string Status, string Mutations, string Tests);

    [GeneratedRegex(@"^\| ([A-Z]{2}-\d{2}) \| (.*?) \| ((?:MUST|SHOULD|COULD)(?: / (?:MUST|SHOULD|COULD))?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|$")]
    private static partial Regex RowPattern();

    private static string WwwRoot(string file) => Path.Combine(Root, "tools", "Lateral.Cms.TestConsole", "wwwroot", file);

    private static JsonDocument Json(string file) => JsonDocument.Parse(File.ReadAllText(WwwRoot(file)));

    private static List<Row> DocumentRows() => File.ReadAllLines(Path.Combine(Root, "docs", "REQUIREMENTS.md"))
        .Select(line => RowPattern().Match(line))
        .Where(m => m.Success)
        .Select(m => new Row(m.Groups[1].Value, m.Groups[6].Value.Trim(), m.Groups[7].Value.Trim(), m.Groups[8].Value.Trim()))
        .ToList();

    private static string Normalise(string text) => Regex.Replace(
        text.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"'), @"\s+", " ").Trim();

    [Fact]
    public void The_generated_requirements_data_matches_the_requirements_document()
    {
        var rows = DocumentRows();
        using var json = Json("requirements.json");
        var generated = json.RootElement.GetProperty("rows").EnumerateArray().ToList();

        Assert.True(rows.Count >= 100, $"only {rows.Count} rows read from docs/REQUIREMENTS.md");
        // If this fails, the document changed: run  node tools/requirements/build.mjs
        Assert.Equal(rows.Select(r => r.Id), generated.Select(g => g.GetProperty("id").GetString()));
        foreach (var (row, element) in rows.Zip(generated))
        {
            Assert.True(row.Status == element.GetProperty("status").GetString(), $"{row.Id}: status differs; run node tools/requirements/build.mjs");
            var names = element.GetProperty("tests").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.True(row.Tests.Split('`').Where((_, i) => i % 2 == 1).SequenceEqual(names), $"{row.Id}: tests differ; run node tools/requirements/build.mjs");
            var mutations = element.GetProperty("mutations").EnumerateArray().Select(m => m.GetString()).ToList();
            Assert.True((row.Mutations == "-" ? [] : row.Mutations.Split(' ')).SequenceEqual(mutations), $"{row.Id}: mutations differ; run node tools/requirements/build.mjs");
        }
    }

    [Fact]
    public void Every_brief_quote_is_the_briefs_own_words()
    {
        var brief = File.ReadAllText(Path.Combine(Root, "docs", "REBUILD-BRIEF.md"));
        var from = brief.IndexOf("## 1. The original brief", StringComparison.Ordinal);
        var original = Normalise(brief[from..brief.IndexOf("\n## 2.", from, StringComparison.Ordinal)]);

        using var trace = Json("brief-trace.json");
        foreach (var section in trace.RootElement.GetProperty("sections").EnumerateArray())
        {
            foreach (var item in section.GetProperty("items").EnumerateArray())
            {
                var quote = Normalise(item.GetProperty("quote").GetString()!);
                Assert.True(original.Contains(quote, StringComparison.Ordinal), $"{item.GetProperty("id")}: this is not in the original brief: {quote}");
            }
        }
    }

    [Fact]
    public void An_item_is_met_only_when_every_requirement_it_cites_is_done_and_anything_less_says_what_is_missing()
    {
        var rows = DocumentRows().ToDictionary(r => r.Id);
        using var trace = Json("brief-trace.json");
        var statuses = trace.RootElement.GetProperty("statuses").EnumerateObject().Select(p => p.Name).ToHashSet();
        var items = trace.RootElement.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("items").EnumerateArray())
            .Concat(trace.RootElement.GetProperty("beyond").GetProperty("items").EnumerateArray()).ToList();

        Assert.Equal(items.Count, items.Select(i => i.GetProperty("id").GetString()).Distinct().Count());
        foreach (var item in items)
        {
            var id = item.GetProperty("id").GetString();
            var status = item.GetProperty("status").GetString()!;
            Assert.Contains(status, statuses);
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("how").GetString()), $"{id} has no answer");

            var cited = item.GetProperty("reqs").EnumerateArray().Select(r => r.GetString()!).ToList();
            foreach (var req in cited)
            {
                Assert.True(rows.ContainsKey(req), $"{id} cites {req}, which is not in docs/REQUIREMENTS.md");
            }

            var hasGap = item.TryGetProperty("gap", out var gap) && !string.IsNullOrWhiteSpace(gap.GetString());
            if (status == "met")
            {
                Assert.False(hasGap, $"{id} is met but lists a gap");
                Assert.True(cited.Count > 0 || item.GetProperty("where").GetArrayLength() > 0, $"{id} is met with nothing to show for it");
                foreach (var req in cited)
                {
                    Assert.True(rows[req].Status.StartsWith("Done", StringComparison.Ordinal), $"{id} is met but cites {req}, which is: {rows[req].Status}");
                }
            }
            else
            {
                Assert.True(hasGap, $"{id} is {status} but does not say what is missing");
            }
        }
    }

    [Fact]
    public void Every_file_and_live_check_the_answers_point_at_exists()
    {
        using var trace = Json("brief-trace.json");
        var items = trace.RootElement.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("items").EnumerateArray())
            .Concat(trace.RootElement.GetProperty("beyond").GetProperty("items").EnumerateArray()).ToList();

        foreach (var item in items)
        {
            foreach (var file in item.GetProperty("where").EnumerateArray())
            {
                Assert.True(File.Exists(Path.Combine(Root, file.GetString()!)), $"{item.GetProperty("id")} names a file that does not exist: {file}");
            }

            if (item.TryGetProperty("live", out var live) && live.TryGetProperty("view", out var view))
            {
                Assert.Contains(view.GetString(), Views);
            }
        }
    }
}
