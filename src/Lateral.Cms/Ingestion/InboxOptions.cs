using System.ComponentModel.DataAnnotations;

namespace Lateral.Cms.Ingestion;

/// <summary>Settings for the queue of accepted batches and the worker that applies them.</summary>
public sealed class InboxOptions
{
    public const string SectionName = "Inbox";

    /// <summary>
    /// Runs the background worker. Switch it off only for tests, which then apply batches deliberately; with it
    /// off, accepted batches wait.
    /// </summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>How long an idle worker sleeps before looking again. A new batch wakes it at once; this covers retries that have come due.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:10:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Attempts before a batch is set aside as failed so it cannot hold up the others. A batch holding an unPublish or delete is never set aside: it keeps retrying at the longest delay.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Wait before the second attempt; it doubles each time up to <see cref="RetryMaxDelay"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long the worker backs off after an unexpected error (for example the database being unreachable) before trying again.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:10:00")]
    public TimeSpan FaultDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Most payload bytes the queue may hold (queued and failed batches together). A batch that would exceed it
    /// is refused with <c>429</c> and nothing stored, so a slow worker pushes back on senders instead of growing without limit.
    /// </summary>
    [Range(1L, long.MaxValue)]
    public long MaxBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>The wait before attempt number <paramref name="attempt"/> (2 = first retry).</summary>
    public TimeSpan DelayBefore(int attempt)
    {
        if (attempt <= 1)
        {
            return TimeSpan.Zero;
        }

        var factor = Math.Pow(2, Math.Min(attempt - 2, 30));
        var ticks = Math.Min(RetryMaxDelay.Ticks, RetryBaseDelay.Ticks * factor);
        return TimeSpan.FromTicks((long)ticks);
    }
}
