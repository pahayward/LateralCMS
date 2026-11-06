using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Api;

public sealed class CommandLoggingAsyncFactory : CmsFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
        [new("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "Information")];
}

/// <summary>Guideline 6.1 for both halves of asynchronous mode: a constant number of queries per batch.</summary>
[Trait("req", "AS-10")]
public class AsyncQueryCountTests(CommandLoggingAsyncFactory factory) : IClassFixture<CommandLoggingAsyncFactory>
{
    private List<string> Commands(int from, int to = int.MaxValue) => [.. factory.Logs.Entries.Skip(from).Take(to - from)
        .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command"
                    && e.Message.StartsWith("Executed DbCommand", StringComparison.Ordinal))
        .Select(e => e.Message)];

    private static int StateReads(IEnumerable<string> commands) => commands.Count(m =>
        m.Contains("SELECT", StringComparison.Ordinal)
        && (m.Contains("FROM \"Entities\"", StringComparison.Ordinal) || m.Contains("FROM \"Tombstones\"", StringComparison.Ordinal)));

    private static int Statements(IEnumerable<string> commands) => commands.Count();

    private async Task<(int AcceptStateReads, int AcceptStatements, int ApplyStateReads, int ApplyStatements)> MeasureAsync(
        params object[] events)
    {
        await factory.DrainAsync();
        var start = factory.Logs.Entries.Count;
        using var _ = await factory.AcceptAsync(events);
        var afterAccept = factory.Logs.Entries.Count;
        await factory.StepAsync();

        var accept = Commands(start, afterAccept);
        var apply = Commands(afterAccept);
        return (StateReads(accept), Statements(accept), StateReads(apply), Statements(apply));
    }

    [Fact]
    public async Task Accepting_reads_no_entity_state_and_applying_reads_it_in_two_queries_however_big_the_batch()
    {
        var one = await MeasureAsync(Evt.Publish(Evt.Id(), 1, 1));
        var sixty = await MeasureAsync([.. Enumerable.Range(0, 60).Select(_ => Evt.Publish(Evt.Id(), 1, 1))]);

        Assert.Equal(0, one.AcceptStateReads);
        Assert.Equal(0, sixty.AcceptStateReads);
        Assert.Equal(2, one.ApplyStateReads); // entities + tombstones
        Assert.Equal(2, sixty.ApplyStateReads);
    }

    [Fact]
    public async Task Many_versions_of_one_entity_are_folded_without_extra_reads()
    {
        var id = Evt.Id();

        var reads = await MeasureAsync([.. Enumerable.Range(1, 30).Select(v => Evt.Publish(id, v, v))]);

        Assert.Equal(2, reads.ApplyStateReads);
    }
}
