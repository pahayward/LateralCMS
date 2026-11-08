using Microsoft.EntityFrameworkCore;

namespace Lateral.Cms.Persistence;

/// <summary>
/// The answer behind <c>/health</c>, which anyone may ask. Opening the database goes through the same process-wide lock the real work
/// uses, so a caller hammering the route could make ingestion wait behind it. A good answer is therefore kept for a few seconds and
/// shared; a bad one is never kept, so recovery shows at once.
/// </summary>
public sealed class HealthProbe(IServiceScopeFactory scopes, TimeProvider time)
{
    private static readonly TimeSpan KeepGoodFor = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _one = new(1, 1);
    private long _goodUntilTicks; // UTC ticks; 0 = never

    public async Task<bool> IsUpAsync(CancellationToken cancellationToken)
    {
        if (time.GetUtcNow().UtcTicks < Volatile.Read(ref _goodUntilTicks))
        {
            return true;
        }

        // One check at a time: a burst of callers waits for the one in flight instead of opening a connection each.
        await _one.WaitAsync(cancellationToken);
        try
        {
            if (time.GetUtcNow().UtcTicks < Volatile.Read(ref _goodUntilTicks))
            {
                return true;
            }

            using var scope = scopes.CreateScope();
            var up = await scope.ServiceProvider.GetRequiredService<CmsReadDbContext>().Database.CanConnectAsync(cancellationToken);
            if (up)
            {
                Volatile.Write(ref _goodUntilTicks, (time.GetUtcNow() + KeepGoodFor).UtcTicks);
            }

            return up;
        }
        finally
        {
            _one.Release();
        }
    }
}
