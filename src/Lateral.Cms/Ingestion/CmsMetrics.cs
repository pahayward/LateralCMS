using System.Diagnostics.Metrics;
using Lateral.Cms.Domain;

namespace Lateral.Cms.Ingestion;

/// <summary>
/// Counters for the ingestion pipeline, exposed through System.Diagnostics.Metrics so any OpenTelemetry
/// or dotnet-counters consumer can pick them up.
/// </summary>
/// <remarks>
/// The two numbers that say whether the asynchronous pipeline is healthy are <c>cms.queue.oldest_age</c> (how long
/// the longest-waiting batch has been waiting: alert if it grows) and <c>cms.queue.failed</c> (batches set aside:
/// alert on anything above zero). A queue that quietly falls behind is the main risk of asynchronous processing.
/// </remarks>
public sealed class CmsMetrics : IDisposable
{
    public const string MeterName = "Lateral.Cms";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _events;
    private readonly Counter<long> _queued;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _setAside;
    private readonly Histogram<double> _batchDuration;
    private readonly Histogram<double> _acceptDuration;
    private readonly Histogram<double> _lag;

    public CmsMetrics(QueueBacklog backlog)
    {
        _events = _meter.CreateCounter<long>("cms.events.processed", description: "Events decided, by outcome and type.");
        _queued = _meter.CreateCounter<long>("cms.events.queued", description: "Events accepted into the queue, by type.");
        _batchDuration = _meter.CreateHistogram<double>("cms.batch.duration", unit: "ms", description: "Time to apply one batch.");
        _acceptDuration = _meter.CreateHistogram<double>("cms.batch.accept.duration", unit: "ms", description: "Time to validate and store one batch in the queue.");
        _lag = _meter.CreateHistogram<double>("cms.queue.lag", unit: "ms", description: "Time a batch waited in the queue before it was applied.");
        _retries = _meter.CreateCounter<long>("cms.batch.retries", description: "Batch attempts that failed and will be retried.");
        _setAside = _meter.CreateCounter<long>("cms.batch.set_aside", description: "Batches given up on after too many failed attempts.");

        _meter.CreateObservableGauge("cms.queue.pending", () => backlog.PendingBatches, description: "Batches waiting to be applied.");
        _meter.CreateObservableGauge("cms.queue.failed", () => backlog.FailedBatches, description: "Batches set aside as failed, waiting for an admin.");
        _meter.CreateObservableGauge("cms.queue.oldest_age", () => backlog.OldestPendingAgeSeconds, unit: "s", description: "Age of the longest-waiting pending batch.");
    }

    // The type tag is bounded to the known set; untrusted text must not create unbounded metric series.
    public void EventProcessed(EventOutcome outcome, CmsEventType? type) =>
        _events.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()),
            new KeyValuePair<string, object?>("type", type?.ToString() ?? "other"));

    public void EventQueued(CmsEventType? type) =>
        _queued.Add(1, new KeyValuePair<string, object?>("type", type?.ToString() ?? "other"));

    public void BatchCompleted(double elapsedMs) => _batchDuration.Record(elapsedMs);

    public void BatchAccepted(double elapsedMs) => _acceptDuration.Record(elapsedMs);

    public void QueueLag(double lagMs) => _lag.Record(lagMs);

    public void BatchRetried() => _retries.Add(1);

    public void BatchSetAside() => _setAside.Add(1);

    public void Dispose() => _meter.Dispose();
}
