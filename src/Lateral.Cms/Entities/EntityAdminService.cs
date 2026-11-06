using Lateral.Cms.Ingestion;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lateral.Cms.Entities;

/// <summary>
/// The admin override: hides an entity from consumers without touching what the CMS told us. Written as
/// single UPDATE statements touching only the override columns, so it can never clobber a concurrent
/// ingestion change (and ingestion never writes these columns).
/// </summary>
public sealed class EntityAdminService(
    CmsWriteDbContext db,
    WriteGate gate,
    TimeProvider time,
    ILogger<EntityAdminService> logger)
{
    /// <summary>Idempotent. Returns the resulting state, or null if the entity does not exist.</summary>
    public async Task<AdminEntityState?> SetDisabledAsync(
        string id, bool disabled, string admin, CancellationToken cancellationToken)
    {
        using var _ = await gate.AcquireAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;

        // The WHERE clause makes a repeat call a no-op, which keeps the original DisabledBy/DisabledAt.
        var changed = disabled
            ? await db.Entities
                .Where(e => e.Id == id && !e.IsDisabledByAdmin)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(e => e.IsDisabledByAdmin, true)
                        .SetProperty(e => e.DisabledBy, admin)
                        .SetProperty(e => e.DisabledAt, (DateTime?)now),
                    cancellationToken)
            : await db.Entities
                .Where(e => e.Id == id && e.IsDisabledByAdmin)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(e => e.IsDisabledByAdmin, false)
                        .SetProperty(e => e.DisabledBy, (string?)null)
                        .SetProperty(e => e.DisabledAt, (DateTime?)null),
                    cancellationToken);

        var state = await db.Entities
            .AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new AdminEntityState(
                e.Id, e.IsPublished, e.IsDisabledByAdmin, e.IsPublished && !e.IsDisabledByAdmin, e.DisabledBy, e.DisabledAt))
            .SingleOrDefaultAsync(cancellationToken);

        if (state is not null)
        {
            IngestionLog.AdminOverride(logger, admin, disabled, id, changed > 0);
        }

        return state;
    }
}
