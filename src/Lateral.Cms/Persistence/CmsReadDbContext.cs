using Microsoft.EntityFrameworkCore;

namespace Lateral.Cms.Persistence;

/// <summary>
/// Context for queries only: no change tracking, and any attempt to save throws. Registered against a
/// read-only SQLite connection, so even a bug cannot write through it.
/// </summary>
public sealed class CmsReadDbContext(DbContextOptions<CmsReadDbContext> options) : CmsDbContext(options)
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("CmsReadDbContext is read-only.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("CmsReadDbContext is read-only.");
}
