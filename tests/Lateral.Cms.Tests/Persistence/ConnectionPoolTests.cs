using Lateral.Cms.Persistence;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Persistence;

/// <summary>
/// Microsoft.Data.Sqlite's connection pool can hand out a connection that another thread is still in the middle of
/// taking, and then fail that thread with "database is locked". It only shows when many requests open connections at
/// the same moment, so this opens a great many at once, on both contexts, and expects every one to work.
/// </summary>
[Trait("req", "ST-03")]
public class ConnectionPoolTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    [Fact]
    public void No_two_connections_are_ever_being_opened_at_once()
    {
        var inside = 0;
        var overlaps = 0;

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            for (var i = 0; i < 200; i++)
            {
                SerialOpenInterceptor.Serially(() =>
                {
                    if (Interlocked.Increment(ref inside) > 1)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    Thread.SpinWait(50);
                    Interlocked.Decrement(ref inside);
                });
            }
        });

        Assert.Equal(0, overlaps);
    }

    [Fact]
    public async Task Opening_many_connections_at_the_same_moment_never_fails()
    {
        _ = factory.Services;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 48), new ParallelOptions { MaxDegreeOfParallelism = 48 }, async (worker, _) =>
            {
                for (var i = 0; i < 400; i++)
                {
                    try
                    {
                        await using var scope = factory.Services.CreateAsyncScope();
                        if (worker % 2 == 0)
                        {
                            Assert.True(await scope.ServiceProvider.GetRequiredService<CmsWriteDbContext>().Entities.CountAsync() >= 0);
                        }
                        else
                        {
                            Assert.True(await scope.ServiceProvider.GetRequiredService<CmsReadDbContext>().Entities.CountAsync() >= 0);
                        }
                    }
                    catch (Exception e)
                    {
                        failures.Add(e.GetType().Name + ": " + e.Message);
                    }
                }
            });

        Assert.True(failures.IsEmpty, $"{failures.Count} failures, for example: {failures.FirstOrDefault()}");
    }
}
