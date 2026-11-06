using System.Net;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Lateral.Cms.Tests.Api;

/// <summary>The same app with its background worker running, as in production. A single failure sets a batch aside.</summary>
public sealed class LiveWorkerFactory : CmsFactory
{
    protected override bool WorkerEnabled => true;

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Inbox:MaxAttempts", "1")];
}

/// <summary>A second start of the app on a database an earlier start left behind, as after a restart or a crash.</summary>
public sealed class RestartedHost(string databasePath, bool worker) : CmsFactory(databasePath, ownsDatabase: false)
{
    protected override bool WorkerEnabled => worker;
}

/// <summary>
/// The running worker: it applies batches on its own, survives faults, and picks up where an earlier run stopped.
/// Waits are for a condition with a time limit, never a fixed sleep.
/// </summary>
[Trait("req", "AS-03")]
[Trait("req", "OB-05")]
[Trait("req", "AS-08")]
public class InboxWorkerTests(LiveWorkerFactory factory) : IClassFixture<LiveWorkerFactory>
{
    private HttpClient Admin => factory.Admin();

    private Task<bool> VisibleAsync(string id) => IsAsync(factory, id);

    private static async Task<bool> IsAsync(CmsFactory host, string id) =>
        (await host.Admin().GetEntityAsync(id)).StatusCode == HttpStatusCode.OK;

    [Fact]
    public async Task An_accepted_batch_is_applied_by_the_worker_without_anyone_asking()
    {
        var id = Evt.Id();

        using var ack = await factory.AcceptAsync(Evt.Publish(id, 1, 1));

        await CmsFactory.WaitUntilAsync(() => VisibleAsync(id), "the entity to be applied");
        await CmsFactory.WaitUntilAsync(
            async () => await factory.StatusNameAsync(CmsFactory.BatchId(ack)) == "completed", "the batch to complete");
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM Events WHERE BatchId = '{CmsFactory.BatchId(ack)}'"));
    }

    [Fact]
    public async Task A_burst_of_senders_is_absorbed_and_every_event_ends_up_applied()
    {
        var ids = Enumerable.Range(0, 60).Select(_ => Evt.Id()).ToArray();

        await Task.WhenAll(ids.Select(async id =>
        {
            using var _ = await factory.AcceptAsync(Evt.Publish(id, 1, 1));
        }));

        await CmsFactory.WaitUntilAsync(
            async () => (await Task.WhenAll(ids.Select(VisibleAsync))).All(v => v), "all 60 entities to be applied");
    }

    [Fact]
    public async Task A_batch_the_worker_cannot_apply_is_set_aside_and_the_worker_carries_on()
    {
        var poison = "poison-" + Guid.NewGuid().ToString("N");
        var healthy = Evt.Id();
        factory.Sql(
            $"CREATE TRIGGER \"fail_{poison}\" BEFORE INSERT ON Entities WHEN NEW.Id = '{poison}' " +
            "BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;");
        string poisoned;
        try
        {
            using var ack = await factory.AcceptAsync(Evt.Publish(poison, 1, 1));
            poisoned = CmsFactory.BatchId(ack);
            await CmsFactory.WaitUntilAsync(async () => await factory.StatusNameAsync(poisoned) == "failed", "the batch to be set aside");
        }
        finally
        {
            factory.Sql($"DROP TRIGGER \"fail_{poison}\"");
        }

        // Still alive: a batch accepted after the failure is applied.
        using var next = await factory.AcceptAsync(Evt.Publish(healthy, 1, 1));
        await CmsFactory.WaitUntilAsync(() => VisibleAsync(healthy), "the worker to apply the next batch");
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(poison)).StatusCode);

        // And the admin can see the failure and put it right.
        Assert.Equal(HttpStatusCode.OK, (await Admin.PostAsync($"/api/admin/ingestion/{poisoned}/requeue", null)).StatusCode);
        await CmsFactory.WaitUntilAsync(() => VisibleAsync(poison), "the requeued batch to be applied");
    }

    [Fact]
    public async Task A_storage_outage_is_logged_and_the_worker_resumes_when_storage_returns()
    {
        var id = Evt.Id();

        // Take the queue table away from under the worker, as an unreachable database would.
        factory.Sql("ALTER TABLE Batches RENAME TO InboxBatches_away");
        try
        {
            await CmsFactory.WaitUntilAsync(
                () => Task.FromResult(factory.Logs.Entries.Any(e =>
                    e.Level == LogLevel.Error && e.Message.Contains("batch worker hit an unexpected error"))),
                "the outage to be logged");
        }
        finally
        {
            factory.Sql("ALTER TABLE InboxBatches_away RENAME TO Batches");
        }

        using var ack = await factory.AcceptAsync(Evt.Publish(id, 1, 1));
        await CmsFactory.WaitUntilAsync(() => VisibleAsync(id), "the worker to recover and apply the batch");
    }

    [Fact]
    public async Task The_admin_report_says_the_worker_is_running()
    {
        var body = await (await Admin.GetAsync("/api/admin/ingestion")).Content.ReadAsStringAsync();

        Assert.Contains("\"workerEnabled\":true", body);
    }

    // ---- restarts ------------------------------------------------------------------------------------------

    [Fact]
    [Trait("req", "AS-08")]
    public async Task Batches_accepted_before_a_restart_are_applied_after_it()
    {
        using var before = new CmsFactory();
        var keep = Evt.Id();
        var takeDown = Evt.Id();
        var first = CmsFactory.BatchId(await before.AcceptAsync(Evt.Publish(keep, 1, 1)));
        var second = CmsFactory.BatchId(await before.AcceptAsync(Evt.UnPublish(takeDown, 2, 2)));
        Assert.Equal(HttpStatusCode.NotFound, (await before.Admin().GetEntityAsync(keep)).StatusCode);

        // The earlier run is gone; the queue it left is on disk.
        using var after = new RestartedHost(before.DatabasePath, worker: true);

        await CmsFactory.WaitUntilAsync(async () => await after.StatusNameAsync(first) == "completed", "the first batch");
        await CmsFactory.WaitUntilAsync(async () => await after.StatusNameAsync(second) == "completed", "the second batch");
        Assert.True(await IsAsync(after, keep));
        Assert.True(await IsAsync(after, takeDown));
    }

    [Fact]
    public async Task Stopping_the_app_stops_the_worker_cleanly_with_no_errors()
    {
        var host = new LiveWorkerFactory();
        _ = host.Services; // start it
        await CmsFactory.WaitUntilAsync(
            () => Task.FromResult(host.Logs.Entries.Any(e => e.Message.Contains("batch worker started"))), "the worker to start");

        host.Dispose();

        Assert.Contains(host.Logs.Entries, e => e.Message.Contains("batch worker stopped"));
        Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Error);
    }
}
