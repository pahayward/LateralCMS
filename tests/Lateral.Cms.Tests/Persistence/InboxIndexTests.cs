using Lateral.Cms.Ingestion;
using Lateral.Cms.Persistence;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Persistence;

/// <summary>
/// Guideline 6.3 for the queue: picking the next batch must stay cheap however long the backlog, so the SQL EF
/// really generates has to be served by <c>IX_Batches_Pick</c> without a sort.
/// </summary>
[Trait("req", "AS-10")]
public class InboxIndexTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    [Fact]
    public void Picking_the_next_batch_walks_the_pick_index_in_order_without_a_sort()
    {
        _ = factory.Services; // make sure the database exists
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsReadDbContext>();

        var plan = QueryPlan.Explain(factory, BatchProcessor.NextDue(db.Batches, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)).Take(1));

        Assert.True(plan.Contains("IX_Batches_Pick"), $"the pick index is not used: {plan}");
        Assert.True(!plan.Contains("TEMP B-TREE"), $"needs a sort: {plan}");
    }

    [Fact]
    public void The_events_of_a_batch_are_read_by_their_index()
    {
        _ = factory.Services;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsReadDbContext>();
        var batchId = Guid.NewGuid();

        var plan = QueryPlan.Explain(factory, db.Events.Where(e => e.BatchId == batchId).OrderBy(e => e.Sequence));

        Assert.True(plan.Contains("IX_Events_BatchId_Sequence"), $"the events index is not used: {plan}");
        Assert.True(!plan.Contains("TEMP B-TREE"), $"needs a sort: {plan}");
    }
}
