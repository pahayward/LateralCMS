using System.Net;
using System.Net.Http.Json;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Lateral.Cms.Tests.Api;

/// <summary>
/// What happens when applying a queued batch fails. A trigger makes the database refuse one specific entity,
/// standing in for any storage fault, so the failure is real and no test hook sits in the production code.
/// </summary>
[Trait("req", "AS-04")]
[Trait("req", "AS-07")]
[Trait("req", "OB-05")]
public class InboxFailureTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private HttpClient Admin => factory.Admin();

    private static string Poison() => "poison-" + Guid.NewGuid().ToString("N");

    private void Break(string entityId) => factory.Sql(
        $"CREATE TRIGGER \"fail_{entityId}\" BEFORE INSERT ON Entities WHEN NEW.Id = '{entityId}' " +
        "BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;");

    private void Repair(string entityId) => factory.Sql($"DROP TRIGGER \"fail_{entityId}\"");

    private async Task<QueueReport> ReportAsync()
    {
        var response = await Admin.GetAsync("/api/admin/ingestion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QueueReport>(Json))!;
    }

    /// <summary>Puts every failed batch back and applies it, so a test leaves the shared queue as it found it.</summary>
    private async Task ClearFailedAsync()
    {
        foreach (var failed in (await ReportAsync()).Failed)
        {
            Assert.Equal(HttpStatusCode.OK, (await Admin.PostAsync($"/api/admin/ingestion/{failed.BatchId}/requeue", null)).StatusCode);
        }

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await factory.DrainAsync();
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseLower) },
    };

    [Fact]
    public async Task A_failed_attempt_applies_nothing_keeps_the_batch_and_waits_before_trying_again()
    {
        await factory.DrainAsync();
        var good = Evt.Id();
        var poison = Poison();
        var batchId = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(good, 1, 1), Evt.Publish(poison, 1, 1)));
        Break(poison);
        try
        {
            Assert.Equal(ProcessStep.Retrying, await factory.StepAsync());

            // All or nothing: the healthy event in the same batch was rolled back with the faulty one.
            Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(good)).StatusCode);
            Assert.Equal(2, factory.Count(
                $"SELECT COUNT(*) FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE AND Outcome = 'queued' AND Payload IS NOT NULL"));

            using var status = await factory.StatusAsync(batchId);
            Assert.Equal("queued", status.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, status.RootElement.GetProperty("attempts").GetInt32());
            Assert.Contains("SqliteException(19)", status.RootElement.GetProperty("lastError").GetString());
            Assert.DoesNotContain("simulated", status.RootElement.GetRawText());

            // Not due yet: nothing happens until the back-off has passed.
            Assert.Equal(ProcessStep.Idle, await factory.StepAsync());
            factory.Time.Advance(TimeSpan.FromSeconds(9));
            Assert.Equal(ProcessStep.Idle, await factory.StepAsync());
            factory.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(ProcessStep.Retrying, await factory.StepAsync());
        }
        finally
        {
            Repair(poison);
        }

        // The fault is gone: the very same batch now goes through, whole.
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await factory.DrainAsync();
        Assert.Equal("completed", await factory.StatusNameAsync(batchId));
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetEntityAsync(good)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetEntityAsync(poison)).StatusCode);
    }

    [Fact]
    [Trait("req", "AS-02")]
    public async Task Applying_the_state_the_outcomes_and_completing_the_batch_succeed_or_fail_together()
    {
        await ClearFailedAsync();
        var id = Evt.Id();
        var other = Evt.Id();
        var batchId = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(id, 1, 1), Evt.Delete(other, 1)));
        var trigger = "stuck_" + batchId.Replace("-", "");

        // Refuse only the last statement, the batch being marked completed. If the apply were a separate commit, the
        // entity, the tombstone and the outcomes would already be stored while the batch still waited, and a retry
        // would apply it twice (and the payload would already be gone).
        factory.Sql(
            $"CREATE TRIGGER \"{trigger}\" BEFORE UPDATE ON Batches WHEN OLD.BatchId = '{batchId.ToUpperInvariant()}' AND NEW.Status = 'completed' " +
            "BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;");
        try
        {
            Assert.Equal(ProcessStep.Retrying, await factory.StepAsync());

            Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetEntityAsync(id)).StatusCode);
            Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM Tombstones WHERE Id = '{other}'"));
            Assert.Equal(1, factory.Count(
                $"SELECT COUNT(*) FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE AND Outcome = 'queued' AND Payload IS NOT NULL"));
            Assert.Equal(2, factory.Count($"SELECT COUNT(*) FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE AND Outcome = 'queued'"));
            Assert.Equal("queued", await factory.StatusNameAsync(batchId));
        }
        finally
        {
            factory.Sql($"DROP TRIGGER \"{trigger}\"");
        }

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await factory.DrainAsync();
        using var done = await factory.StatusAsync(batchId);
        Assert.Equal(["applied", "applied"], done.Outcomes());
        Assert.Equal(2, factory.Count($"SELECT COUNT(*) FROM Events WHERE BatchId = '{batchId}' COLLATE NOCASE AND Outcome = 'applied' AND Payload IS NULL"));
    }

    [Fact]
    public async Task The_wait_between_attempts_doubles_up_to_a_ceiling()
    {
        await factory.DrainAsync();
        var poison = Poison();
        var batchId = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(poison, 1, 1)));
        Break(poison);
        try
        {
            Assert.Equal(ProcessStep.Retrying, await factory.StepAsync()); // attempt 1 fails; next in 10 s
            factory.Time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(ProcessStep.Retrying, await factory.StepAsync()); // attempt 2 fails; next in 20 s

            factory.Time.Advance(TimeSpan.FromSeconds(19));
            Assert.Equal(ProcessStep.Idle, await factory.StepAsync());
            factory.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(ProcessStep.SetAside, await factory.StepAsync()); // attempt 3 is the last
        }
        finally
        {
            Repair(poison);
        }

        Assert.Equal("failed", await factory.StatusNameAsync(batchId));
        await ClearFailedAsync();
        Assert.Equal("completed", await factory.StatusNameAsync(batchId));
    }

    [Fact]
    public async Task A_batch_that_keeps_failing_is_set_aside_and_does_not_hold_up_the_ones_behind_it()
    {
        await factory.DrainAsync();
        var poison = Poison();
        var healthy = Evt.Id();
        var poisoned = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(poison, 1, 1)));
        var fine = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(healthy, 1, 1)));
        Break(poison);
        try
        {
            // The poisoned batch is first in line and fails; the healthy one is applied straight after it.
            Assert.Equal(ProcessStep.Retrying, await factory.StepAsync());
            Assert.Equal(ProcessStep.Applied, await factory.StepAsync());
            Assert.Equal("completed", await factory.StatusNameAsync(fine));
            Assert.Equal(HttpStatusCode.OK, (await Admin.GetEntityAsync(healthy)).StatusCode);

            factory.Time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(ProcessStep.Retrying, await factory.StepAsync());
            factory.Time.Advance(TimeSpan.FromSeconds(20));
            Assert.Equal(ProcessStep.SetAside, await factory.StepAsync());
        }
        finally
        {
            Repair(poison);
        }

        using var status = await factory.StatusAsync(poisoned);
        Assert.Equal("failed", status.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, status.RootElement.GetProperty("attempts").GetInt32());
        Assert.Equal(["queued"], status.Outcomes());

        // Set aside means set aside: it is not picked up again, however long the worker waits.
        factory.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ProcessStep.Idle, await factory.StepAsync());
        Assert.Contains(factory.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(poisoned) && e.Message.Contains("set aside"));
        await ClearFailedAsync();
    }

    [Fact]
    [Trait("req", "AS-04")]
    public async Task A_batch_holding_a_takedown_is_never_set_aside_it_keeps_retrying_at_the_longest_delay_until_it_succeeds()
    {
        await ClearFailedAsync();
        var poison = Poison();
        var batchId = CmsFactory.BatchId(await factory.AcceptAsync(Evt.UnPublish(poison, 2, 2)));
        Break(poison);
        try
        {
            // Far more attempts than Inbox:MaxAttempts (3): an ordinary batch would have been set aside long ago.
            for (var attempt = 1; attempt <= 8; attempt++)
            {
                factory.Time.Advance(TimeSpan.FromMinutes(5));
                Assert.Equal(ProcessStep.Retrying, await factory.StepAsync());
            }

            using (var status = await factory.StatusAsync(batchId))
            {
                Assert.Equal("queued", status.RootElement.GetProperty("status").GetString());
                Assert.Equal(8, status.RootElement.GetProperty("attempts").GetInt32());
            }

            Assert.Equal(0, (await ReportAsync()).FailedBatches);
            Assert.Contains(factory.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(batchId) && e.Message.Contains("stay visible"));

            // The delay stops growing at Inbox:RetryMaxDelay (60 s).
            factory.Time.Advance(TimeSpan.FromSeconds(59));
            Assert.Equal(ProcessStep.Idle, await factory.StepAsync());
            factory.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(ProcessStep.Retrying, await factory.StepAsync());
        }
        finally
        {
            Repair(poison);
        }

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await factory.DrainAsync();
        Assert.Equal("completed", await factory.StatusNameAsync(batchId));
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetEntityAsync(poison)).StatusCode);
    }

    [Fact]
    public async Task An_admin_sees_the_failed_batch_and_can_requeue_it_once_the_fault_is_fixed()
    {
        await ClearFailedAsync();
        var poison = Poison();
        var batchId = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(poison, 1, 1, new { title = "report-sentinel" })));
        Break(poison);
        try
        {
            for (var i = 0; i < 3; i++)
            {
                factory.Time.Advance(TimeSpan.FromMinutes(5));
                await factory.StepAsync();
            }
        }
        finally
        {
            Repair(poison);
        }

        var report = await ReportAsync();
        Assert.Equal(1, report.FailedBatches);
        Assert.Equal(batchId, report.Failed.Single().BatchId.ToString());
        Assert.Equal(3, report.Failed.Single().Attempts);
        var raw = await (await Admin.GetAsync("/api/admin/ingestion")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("report-sentinel", raw);

        var requeue = await Admin.PostAsync($"/api/admin/ingestion/{batchId}/requeue", null);
        Assert.Equal(HttpStatusCode.OK, requeue.StatusCode);
        Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("requeued failed batch") && e.Message.Contains(batchId));
        Assert.Equal(1, factory.Count($"SELECT COUNT(*) FROM AuditLog WHERE Action = 'batch.requeued' AND Target = '{batchId}' COLLATE NOCASE AND Actor = '{CmsFactory.AdminName}'"));

        await factory.DrainAsync();
        Assert.Equal("completed", await factory.StatusNameAsync(batchId));
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetEntityAsync(poison)).StatusCode);
        Assert.Equal(0, (await ReportAsync()).FailedBatches);
    }

    [Fact]
    public async Task Only_a_failed_batch_can_be_requeued()
    {
        await factory.DrainAsync();
        var batchId = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(Evt.Id(), 1, 1)));

        var waiting = await Admin.PostAsync($"/api/admin/ingestion/{batchId}/requeue", null);
        var unknown = await Admin.PostAsync($"/api/admin/ingestion/{Guid.NewGuid()}/requeue", null);

        Assert.Equal(HttpStatusCode.Conflict, waiting.StatusCode);
        using (var body = await waiting.ReadJsonAsync())
        {
            Assert.Equal("batch_not_failed", body.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await factory.DrainAsync();
    }

    [Fact]
    public async Task The_report_shows_how_much_is_waiting_and_for_how_long()
    {
        await ClearFailedAsync();
        await factory.AcceptAsync(Evt.Publish(Evt.Id(), 1, 1), Evt.Publish(Evt.Id(), 1, 1));
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        await factory.AcceptAsync(Evt.UnPublish(Evt.Id(), 2, 2));
        factory.Time.Advance(TimeSpan.FromSeconds(15));

        var report = await ReportAsync();

        Assert.False(report.WorkerEnabled);
        Assert.Equal(2, report.PendingBatches);
        Assert.Equal(3, report.PendingEvents);
        Assert.Equal(45, report.OldestPendingAgeSeconds);
        Assert.True(report.UsedBytes > 0);
        Assert.True(report.MaxBytes >= report.UsedBytes);

        await factory.DrainAsync();
        var drained = await ReportAsync();
        Assert.Equal(0, drained.PendingBatches);
        Assert.Null(drained.OldestPendingAgeSeconds);
        Assert.Equal(0, drained.UsedBytes);
    }

    [Fact]
    public async Task Queue_age_and_lag_are_measured_so_a_backlog_cannot_grow_unseen()
    {
        await factory.DrainAsync();
        const double lagMs = 12_345_000; // distinctive, so other tests' measurements cannot be mistaken for ours
        var seen = new List<double>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CmsMetrics.MeterName && instrument.Name == "cms.queue.lag")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            lock (seen)
            {
                seen.Add(value);
            }
        });
        listener.Start();

        await factory.AcceptAsync(Evt.Publish(Evt.Id(), 1, 1));
        factory.Time.Advance(TimeSpan.FromMilliseconds(lagMs));
        await factory.DrainAsync();

        lock (seen)
        {
            Assert.Contains(lagMs, seen);
        }
    }
}
