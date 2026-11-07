using System.Diagnostics.Metrics;
using System.Text.Json;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lateral.Cms.Tests.Api;

/// <summary>What an operator can see: one log line per event, the batch id everywhere, and a meter with bounded tags.</summary>
public class ObservabilityTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private IEnumerable<CapturingLoggerProvider.Entry> LinesFor(string needle) =>
        factory.Logs.Entries.Where(e => e.Message.Contains(needle, StringComparison.Ordinal)).ToList();

    // ---- OB-01: one line per event, with the right level ---------------------------------------------------

    [Fact]
    [Trait("req", "OB-01")]
    public async Task Every_event_is_logged_once_with_its_batch_index_id_type_version_outcome_reason_and_checksum()
    {
        var id = Evt.Id();
        using var status = await factory.SendAsync(
            Evt.Publish(id, 1, 1, new { a = 1 }),
            Evt.Publish(id, 0, 1),
            new { type = "mystery", id, timestamp = Evt.T(1) });
        var batchId = status.RootElement.GetProperty("batchId").GetString()!;
        var checksum = status.RootElement.GetProperty("results")[0].GetProperty("checksum").GetString()!;

        var lines = LinesFor($"Batch {batchId} event").ToList();

        Assert.Equal(3, lines.Count);
        var applied = Assert.Single(lines, l => l.Message.Contains("event 0 "));
        Assert.Contains($"publish entity {id} version 1: Applied", applied.Message);
        Assert.Contains(checksum, applied.Message);
        var rejected = Assert.Single(lines, l => l.Message.Contains("event 1 "));
        Assert.Contains(": Rejected invalid_version", rejected.Message);
        var unsupported = Assert.Single(lines, l => l.Message.Contains("event 2 "));
        Assert.Contains("mystery", unsupported.Message);
        Assert.Contains("IgnoredUnsupportedType unsupported_type", unsupported.Message);
    }

    [Fact]
    [Trait("req", "OB-01")]
    public async Task Applied_and_duplicate_are_information_while_stale_unsupported_rejected_and_conflict_winners_are_warnings()
    {
        var id = Evt.Id();
        var conflict = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 2, 5, new { a = 1 }), Evt.Publish(id, 2, 5, new { a = 1 }));
        await factory.SendAsync(Evt.Publish(id, 1, 9)); // older version: stale
        await factory.SendAsync(Evt.Publish(conflict, 1, 1, new { t = "a" }), Evt.Publish(conflict, 1, 1, new { t = "b" }));
        await factory.SendAsync(Evt.Publish(Evt.Id(), 0, 1), new { type = "mystery", id, timestamp = Evt.T(1) });

        LogLevel LevelOf(string id, string outcome, string? reason = null) =>
            Assert.Single(
                factory.Logs.Entries,
                e => e.Message.Contains($"entity {id} ") && e.Message.Contains($": {outcome} {reason} checksum") && e.Message.Contains(" event ")).Level;

        Assert.Equal(LogLevel.Information, LevelOf(id, "Applied"));
        Assert.Equal(LogLevel.Information, LevelOf(id, "IgnoredDuplicate"));
        Assert.Equal(LogLevel.Warning, LevelOf(id, "IgnoredStale", "older_version"));
        Assert.Equal(LogLevel.Information, LevelOf(conflict, "Applied", null));
        Assert.Contains(
            factory.Logs.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains($"entity {conflict} ") && e.Message.Contains("Applied same_version_different_content"));
        Assert.Contains(factory.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Rejected invalid_version"));
        Assert.Contains(factory.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("IgnoredUnsupportedType"));
    }

    // ---- OB-02: the batch id is the correlation id everywhere ----------------------------------------------

    [Fact]
    [Trait("req", "OB-02")]
    public async Task The_batch_id_is_the_response_header_the_body_every_log_line_and_the_rows()
    {
        var id = Evt.Id();
        var response = await factory.Cms().PostAsync(Evt.Publish(id, 1, 1), new { type = "mystery", id, timestamp = Evt.T(1) });
        var header = response.Headers.GetValues(IngestionEndpoints.CorrelationHeader).Single();
        using var body = await response.ReadJsonAsync();
        await factory.DrainAsync();

        Assert.Equal(header, body.RootElement.GetProperty("batchId").GetString());
        Assert.Equal(1, factory.Count($"SELECT COUNT(*) FROM Batches WHERE BatchId = '{header}' COLLATE NOCASE"));
        Assert.Equal(2, factory.Count($"SELECT COUNT(*) FROM Events WHERE BatchId = '{header}' COLLATE NOCASE"));

        var mine = LinesFor(header).ToList();
        Assert.Contains(mine, l => l.Message.Contains("accepted"));
        Assert.Contains(mine, l => l.Message.Contains("applied"));
        Assert.Equal(2, mine.Count(l => l.Message.Contains(" event ")));
    }

    // ---- OB-04: the meter ----------------------------------------------------------------------------------

    private sealed record Measurement(string Instrument, long Value, Dictionary<string, string?> Tags);

    private static MeterListener Listen(List<Measurement> sink, HashSet<string> published)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != CmsMetrics.MeterName)
            {
                return;
            }

            lock (published)
            {
                published.Add(instrument.Name);
            }

            l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var map = new Dictionary<string, string?>();
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value?.ToString();
            }

            lock (sink)
            {
                sink.Add(new Measurement(instrument.Name, value, map));
            }
        });
        listener.Start();
        return listener;
    }

    [Fact]
    [Trait("req", "OB-04")]
    public async Task The_meter_counts_events_by_outcome_and_type_and_an_unknown_type_cannot_create_a_new_series()
    {
        var sink = new List<Measurement>();
        var published = new HashSet<string>();
        using var listener = Listen(sink, published);
        var id = Evt.Id();

        // Hostile type strings must not become metric tags.
        await factory.SendAsync(
            Evt.Publish(id, 1, 1),
            new { type = "x-" + Guid.NewGuid(), id, timestamp = Evt.T(1) },
            new { type = "y-" + Guid.NewGuid(), id, timestamp = Evt.T(1) },
            Evt.Delete(id, 5));

        Measurement[] measurements;
        lock (sink)
        {
            measurements = [.. sink];
        }

        var processed = measurements.Where(m => m.Instrument == "cms.events.processed").ToList();
        Assert.NotEmpty(processed);
        var types = processed.Select(m => m.Tags["type"]).Distinct().ToHashSet();
        Assert.Subset(new HashSet<string?> { "Publish", "UnPublish", "Delete", "other" }, types);
        Assert.Contains("other", types);
        Assert.Contains(processed, m => m.Tags["outcome"] == "Applied" && m.Tags["type"] == "Publish");
        Assert.Contains(measurements, m => m.Instrument == "cms.events.queued");

        lock (published)
        {
            Assert.Superset(
                new HashSet<string>
                {
                    "cms.events.processed", "cms.events.queued", "cms.batch.duration", "cms.batch.accept.duration", "cms.queue.lag",
                    "cms.batch.retries", "cms.batch.set_aside", "cms.queue.pending", "cms.queue.failed", "cms.queue.failed_bytes", "cms.queue.oldest_age",
                },
                published);
        }
    }

    [Fact]
    [Trait("req", "OB-04")]
    public async Task The_queue_gauges_report_pending_batches_their_age_and_failures_from_the_backlog_the_worker_refreshes()
    {
        await factory.DrainAsync();
        await factory.AcceptAsync(Evt.Publish(Evt.Id(), 1, 1));
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        await factory.AcceptAsync(Evt.UnPublish(Evt.Id(), 2, 2));
        factory.Time.Advance(TimeSpan.FromSeconds(15));

        await factory.RefreshBacklogAsync();
        var backlog = factory.Services.GetRequiredService<QueueBacklog>();

        Assert.Equal(2, backlog.PendingBatches);
        Assert.Equal(0, backlog.FailedBatches);
        Assert.Equal(45, backlog.OldestPendingAgeSeconds);

        var published = new HashSet<string>();
        using var listener = Listen([], published);
        _ = factory.Services.GetRequiredService<CmsMetrics>();
        lock (published)
        {
            Assert.Contains("cms.queue.pending", published);
            Assert.Contains("cms.queue.oldest_age", published);
            Assert.Contains("cms.queue.failed", published);
        }

        await factory.DrainAsync();
        await factory.RefreshBacklogAsync();
        Assert.Equal(0, backlog.PendingBatches);
        Assert.Equal(0, backlog.OldestPendingAgeSeconds);
    }
}
