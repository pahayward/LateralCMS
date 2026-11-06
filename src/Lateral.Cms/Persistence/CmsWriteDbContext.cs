using Microsoft.EntityFrameworkCore;

namespace Lateral.Cms.Persistence;

/// <summary>Tracking context used by the batch processor, the accept service and the admin override. Owns the migrations.</summary>
public sealed class CmsWriteDbContext(DbContextOptions<CmsWriteDbContext> options) : CmsDbContext(options)
{
    // The concurrency token is a persistence concern, so it is renewed here rather than in the (deterministic)
    // domain rules: every entity written by a save gets a new one, and a save based on a stale read fails.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RenewConcurrencyStamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RenewConcurrencyStamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RenewConcurrencyStamps()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<EntityRow>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.ConcurrencyStamp = Guid.NewGuid();
        }
    }
}
