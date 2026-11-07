using System.ComponentModel.DataAnnotations;
using Lateral.Cms.Integrity;

namespace Lateral.Cms.Ingestion;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Largest accepted request body, judged by the declared length before any of it is read.</summary>
    [Range(1, int.MaxValue)]
    public int MaxBodyBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Largest accepted number of events per batch.</summary>
    [Range(1, int.MaxValue)]
    public int MaxBatchSize { get; set; } = 500;

    /// <summary>Largest accepted canonical payload of a single event.</summary>
    [Range(1, int.MaxValue)]
    public int MaxPayloadBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Batches being read and validated at once. Each holds up to <see cref="MaxBodyBytes"/> in memory, so this bounds the memory
    /// a burst of (authenticated) senders can use. Others wait in a queue; past <see cref="AcceptQueueLimit"/> they get <c>429</c>.
    /// </summary>
    [Range(1, 1024)]
    public int MaxConcurrentAccepts { get; set; } = 16;

    [Range(0, 100_000)]
    public int AcceptQueueLimit { get; set; } = 256;

    /// <summary>Deepest accepted JSON nesting (guards the parser against hostile input).</summary>
    [Range(1, 1024)]
    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>
    /// Events stamped further in the future than this are rejected. A bogus far-future timestamp would
    /// otherwise outrank every legitimate later event for that entity.
    /// </summary>
    public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Algorithm for payload checksums: "sha256" (default) or "md5". Stored values name their own algorithm,
    /// so changing this only affects checksums computed from now on.
    /// </summary>
    [AllowedValues(HashPayloadChecksum.Sha256, HashPayloadChecksum.Md5)]
    public string ChecksumAlgorithm { get; set; } = HashPayloadChecksum.Sha256;
}
