using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Lateral.Cms.Tests.Infrastructure;

/// <summary>Asks SQLite how it would run the SQL that EF Core really generates for a query (guideline 6.3).</summary>
public static class QueryPlan
{
    /// <summary>EF prints parameters as `.param set @name value` lines; inline them so the SQL can be explained.</summary>
    public static string Inline(string queryString)
    {
        var values = new Dictionary<string, string>();
        var sql = new List<string>();
        foreach (var line in queryString.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var m = Regex.Match(line, @"^\.param set (@\S+) (.*)$");
            if (m.Success)
            {
                values[m.Groups[1].Value] = m.Groups[2].Value;
            }
            else
            {
                sql.Add(line);
            }
        }

        var text = string.Join("\n", sql);
        foreach (var (name, value) in values.OrderByDescending(v => v.Key.Length))
        {
            text = text.Replace(name, value);
        }

        return text;
    }

    /// <summary>The plan, one step per segment, e.g. "SEARCH i USING INDEX IX_x (a=?) | USE TEMP B-TREE FOR ORDER BY".</summary>
    public static string Explain<T>(CmsFactory factory, IQueryable<T> query)
    {
        var rows = factory.Sql("EXPLAIN QUERY PLAN " + Inline(query.ToQueryString()));
        return string.Join(" | ", rows.Select(r => (string)r[3]!));
    }
}
