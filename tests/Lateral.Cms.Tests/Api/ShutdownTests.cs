using System.Collections.Concurrent;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Lateral.Cms.Tests.Api;

/// <summary>
/// Stopping the app while its workers are in the middle of opening a database connection cancels that open. That is a normal
/// way to stop, so it must not be logged as an error. One host stopping rarely hits the moment, so many are stopped at once.
/// </summary>
[Trait("req", "OB-05")]
public class ShutdownTests
{
    [Fact]
    public async Task Many_apps_stopped_at_once_log_no_errors()
    {
        var errors = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(Enumerable.Range(0, 60), new ParallelOptions { MaxDegreeOfParallelism = 12 }, async (_, _) =>
        {
            var host = new LiveWorkerFactory();
            _ = host.Services;
            await CmsFactory.WaitUntilAsync(
                () => Task.FromResult(host.Logs.Entries.Any(e => e.Message.Contains("batch worker started"))), "the worker to start");

            host.Dispose();

            foreach (var entry in host.Logs.Entries.Where(e => e.Level >= LogLevel.Error))
            {
                errors.Add(entry.Category + ": " + entry.Message + " " + entry.Exception);
            }
        });

        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine + "----" + Environment.NewLine, errors.Take(2)));
    }
}
