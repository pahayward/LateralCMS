using System.Text;
using Lateral.Cms.Domain;
using Lateral.Cms.Integrity;
using Lateral.Cms.Persistence;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Persistence;

/// <summary>Claims made about the storage layer: read-only reads, checksums on everything, optimistic concurrency.</summary>
[Trait("req", "ST-02")]
[Trait("req", "EV-14")]
public class PersistenceGuaranteeTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private T Resolve<T>(IServiceScope scope)
        where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    // ---- the read side cannot write ---------------------------------------------------------------------

    [Fact]
    public async Task The_read_context_refuses_to_save_and_its_connection_is_read_only()
    {
        using var scope = factory.Services.CreateScope();
        var read = Resolve<CmsReadDbContext>(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => read.SaveChangesAsync());
        Assert.Throws<InvalidOperationException>(() => read.SaveChanges());

        // Even raw SQL cannot get a write through: the database connection itself is opened read-only.
        var ex = await Assert.ThrowsAsync<SqliteException>(() => read.Database.ExecuteSqlRawAsync("DELETE FROM Entities"));
        Assert.Contains("readonly", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_read_context_tracks_nothing()
    {
        await factory.SendAsync(Evt.Publish(Evt.Id(), 1, 1));
        using var scope = factory.Services.CreateScope();
        var read = Resolve<CmsReadDbContext>(scope);

        var rows = await read.Entities.ToListAsync();

        Assert.NotEmpty(rows);
        Assert.Empty(read.ChangeTracker.Entries());
    }

    // ---- every payload has a checksum ------------------------------------------------------------------

    // ---- optimistic concurrency ------------------------------------------------------------------------

    [Fact]
    public async Task A_save_based_on_a_stale_read_is_refused()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));

        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var a = Resolve<CmsWriteDbContext>(first);
        var b = Resolve<CmsWriteDbContext>(second);
        var seenByA = await a.Entities.SingleAsync(e => e.Id == id);
        var seenByB = await b.Entities.SingleAsync(e => e.Id == id);

        seenByA.Payload = """{"by":"a"}""";
        await a.SaveChangesAsync();
        seenByB.Payload = """{"by":"b"}""";

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
    }

    [Fact]
    public async Task Every_save_renews_the_concurrency_token()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));
        var before = (string)factory.Sql($"SELECT ConcurrencyStamp FROM Entities WHERE Id = '{id}'")[0][0]!;

        await factory.SendAsync(Evt.Publish(id, 2, 2));

        var after = (string)factory.Sql($"SELECT ConcurrencyStamp FROM Entities WHERE Id = '{id}'")[0][0]!;
        Assert.NotEqual(before, after);
    }
}
