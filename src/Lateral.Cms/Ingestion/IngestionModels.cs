using System.Text.Json.Serialization;
using Lateral.Cms.Domain;

namespace Lateral.Cms.Ingestion;

/// <summary>
/// Outcome of one event of a batch, as reported back to the sender. <see cref="Checksum"/> is the checksum of
/// the payload that event carried (present for publish/unPublish events that passed validation, whatever
/// their outcome): for an applied or duplicate event it is also the checksum of what is now stored.
/// </summary>
public sealed record EventResult(
    int Index,
    string? Id,
    string? Type,
    long? Version,
    EventOutcome Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Checksum);

/// <summary>
/// Body of <c>POST /cms/events</c> (<c>200</c> when complete, <c>202</c> when events are queued) and of
/// <c>GET /cms/batches/{id}</c>.
/// </summary>
public sealed record IngestResult(
    Guid BatchId,
    BatchState Status,
    int Received,
    int Queued,
    int Applied,
    int Ignored,
    int Rejected,
    IReadOnlyList<EventResult> Results,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? ReceivedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? CompletedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Attempts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastError = null)
{
    /// <summary>Tallies the results into a response.</summary>
    public static IngestResult From(
        Guid batchId, BatchState status, IReadOnlyList<EventResult> results, DateTime? receivedAt = null,
        DateTime? completedAt = null, int? attempts = null, string? lastError = null)
    {
        int queued = 0, applied = 0, ignored = 0, rejected = 0;
        foreach (var result in results)
        {
            switch (result.Outcome)
            {
                case EventOutcome.Queued: queued++; break;
                case EventOutcome.Applied: applied++; break;
                case EventOutcome.Rejected: rejected++; break;
                default: ignored++; break;
            }
        }

        return new IngestResult(
            batchId, status, results.Count, queued, applied, ignored, rejected, results, receivedAt, completedAt, attempts,
            lastError);
    }
}
