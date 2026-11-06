using Lateral.Cms.Domain;
using Lateral.Cms.Persistence;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Persistence;

/// <summary>
/// Guideline 6.3: read queries rely on an index chosen for the real filter. This checks the SQL EF Core
/// actually generates for the listing, not a hand-written approximation of it.
/// </summary>
[Trait("req", "AP-06")]
public class ListingIndexTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private string Plan(IQueryable<EntityRow> query) => QueryPlan.Explain(factory, query);

    private (IServiceScope Scope, CmsReadDbContext Db) Read()
    {
        var scope = factory.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<CmsReadDbContext>());
    }

    [Fact]
    public void The_user_listing_is_served_from_an_index_in_id_order_without_a_sort()
    {
        var (scope, db) = Read();
        using (scope)
        {
            var first = db.Entities.Where(e => e.IsPublished && !e.IsDisabledByAdmin).OrderBy(e => e.Id).Take(51);
            var next = db.Entities.Where(e => e.IsPublished && !e.IsDisabledByAdmin)
                .Where(e => string.Compare(e.Id, "item-003") > 0).OrderBy(e => e.Id).Take(51);

            foreach (var plan in new[] { Plan(first), Plan(next) })
            {
                Assert.True(plan.Contains("IX_Entities_Visible"), $"the visible-rows index is not used: {plan}");
                Assert.True(!plan.Contains("TEMP B-TREE"), $"needs a sort: {plan}");
            }
        }
    }

    [Fact]
    public void The_admin_listing_walks_the_primary_key_in_order_without_a_sort()
    {
        var (scope, db) = Read();
        using (scope)
        {
            var plan = Plan(db.Entities.OrderBy(e => e.Id).Take(51));

            Assert.True(!plan.Contains("TEMP B-TREE"), $"needs a sort: {plan}");
        }
    }

    [Fact]
    public void Fetching_one_entity_uses_the_primary_key()
    {
        var (scope, db) = Read();
        using (scope)
        {
            var plan = Plan(db.Entities.Where(e => e.Id == "x"));

            Assert.True(plan.Contains("SEARCH") && plan.Contains("INDEX", StringComparison.OrdinalIgnoreCase), $"not a key lookup: {plan}");
        }
    }
}
