using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Api;

/// <summary>Guideline 6.1: a constant number of queries per batch, however many events it holds.</summary>
[Trait("req", "AP-06")]
public class QueryCountTests : IClassFixture<QueryCountTests.CommandLoggingFactory>
{
    public sealed class CommandLoggingFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
            [new("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "Information")];
    }

    private readonly CommandLoggingFactory _factory;

    public QueryCountTests(CommandLoggingFactory factory) => _factory = factory;

    /// <summary>Entity and tombstone reads issued while the batch was processed.</summary>
    private async Task<int> StateReadsAsync(params object[] events)
    {
        // Boot the app first: its start-up work (migrations, checksum repair) is not part of a batch.
        var cms = _factory.Cms();
        var before = _factory.Logs.Entries.Count;
        using var result = await _factory.SendAsync(events);
        Assert.Equal(events.Length, result.RootElement.GetProperty("received").GetInt32());

        var commands = _factory.Logs.Entries.Skip(before)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command"
                        && e.Message.StartsWith("Executed DbCommand", StringComparison.Ordinal))
            .Select(e => e.Message)
            .ToList();
        var reads = commands.Where(m => m.Contains("SELECT", StringComparison.Ordinal)
                                        && (m.Contains("FROM \"Entities\"", StringComparison.Ordinal)
                                            || m.Contains("FROM \"Tombstones\"", StringComparison.Ordinal))).ToList();

        // Keep the evidence in the failure message.
        Assert.True(reads.Count <= 2, string.Join("\n---\n", commands));
        return reads.Count;
    }

    [Fact]
    public async Task One_event_and_many_events_cost_the_same_number_of_reads()
    {
        var single = await StateReadsAsync(Evt.Publish(Evt.Id(), 1, 1));
        var sixty = await StateReadsAsync([.. Enumerable.Range(0, 60).Select(_ => Evt.Publish(Evt.Id(), 1, 1))]);

        Assert.Equal(2, single); // entities + tombstones
        Assert.Equal(single, sixty);
    }

    [Fact]
    public async Task Many_versions_of_one_entity_in_a_batch_are_folded_without_extra_reads()
    {
        var id = Evt.Id();

        var reads = await StateReadsAsync([.. Enumerable.Range(1, 30).Select(v => Evt.Publish(id, v, v))]);

        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task An_all_invalid_batch_reads_no_entity_state_at_all()
    {
        var reads = await StateReadsAsync(
            new { type = "publish", id = Evt.Id(), version = 0, payload = new { }, timestamp = Evt.T(1) },
            new { type = "mystery", id = Evt.Id(), timestamp = Evt.T(1) });

        Assert.Equal(0, reads);
    }
}
