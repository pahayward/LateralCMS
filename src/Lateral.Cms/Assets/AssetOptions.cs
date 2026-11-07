using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Assets;

public sealed class AssetOptions
{
    public const string SectionName = "Assets";

    /// <summary>Folder for asset bytes (quarantine, parts, blobs). A relative path is resolved against the content root.</summary>
    [Required]
    public string Root { get; set; } = "App_Data/assets";

    /// <summary>Largest object a single <c>PUT</c> may carry. Anything bigger goes through a multipart upload.</summary>
    [Range(1L, long.MaxValue)]
    public long MaxSimpleBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>Largest object of any kind.</summary>
    [Range(1L, long.MaxValue)]
    public long MaxObjectBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Smallest part of a multipart upload, except the last. S3 uses 5 MiB.</summary>
    [Range(1L, long.MaxValue)]
    public long MinPartBytes { get; set; } = 5L * 1024 * 1024;

    [Range(1L, long.MaxValue)]
    public long MaxPartBytes { get; set; } = 64L * 1024 * 1024;

    [Range(1, 100_000)]
    public int MaxParts { get; set; } = 10_000;

    /// <summary>Most bytes the store may hold (assets that are not rejected, plus parts in flight). Beyond it: <c>429</c>, nothing stored.</summary>
    [Range(1L, long.MaxValue)]
    public long MaxBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>How long an unfinished multipart upload is kept.</summary>
    public TimeSpan UploadTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Concurrent downloads one caller may have open. More get <c>429</c>.</summary>
    [Range(1, 1000)]
    public int MaxConcurrentDownloadsPerCaller { get; set; } = 8;

    /// <summary>Runs the scan worker and the sweeper. Tests switch it off and drive them.</summary>
    public bool WorkerEnabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:00:00.010", "00:10:00")]
    public TimeSpan ScanPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    [Range(1, 100)]
    public int ScanMaxAttempts { get; set; } = 5;

    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan ScanRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan ScanRetryMaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A scan that takes longer than this counts as a failed attempt and is retried. It never counts as clean.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "01:00:00")]
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Files younger than this are never treated as orphans, so the sweeper cannot take one that is still being registered.</summary>
    public TimeSpan SweepGrace { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Which scanner to use. <c>Mock</c> is the only one built in; a real engine is another <see cref="IAssetScanner"/>.</summary>
    [AllowedValues("Mock")]
    public string Scanner { get; set; } = "Mock";

    public MockScannerOptions Mock { get; set; } = new();

    /// <summary>The wait before scan attempt number <paramref name="attempt"/> (2 = first retry).</summary>
    public TimeSpan ScanDelayBefore(int attempt)
    {
        if (attempt <= 1)
        {
            return TimeSpan.Zero;
        }

        var ticks = Math.Min(ScanRetryMaxDelay.Ticks, ScanRetryBaseDelay.Ticks * Math.Pow(2, Math.Min(attempt - 2, 30)));
        return TimeSpan.FromTicks((long)ticks);
    }
}

public sealed class MockScannerOptions
{
    /// <summary>
    /// Lets the content <c>MOCK-SCAN-ERROR</c> / <c>MOCK-SCAN-TIMEOUT</c> simulate a scanner that fails or hangs, so the
    /// fail-closed path can be exercised for real. Off by default; only tests and local trials turn it on.
    /// </summary>
    public bool EnableFailureMarkers { get; set; }

    /// <summary>Also treat the industry EICAR test file as malware. Off by default: antivirus on a developer machine deletes it.</summary>
    public bool DetectEicar { get; set; }

    /// <summary>A bare executable is flagged as malware unless this is on.</summary>
    public bool AllowExecutables { get; set; }

    /// <summary>Largest total uncompressed size an archive may declare.</summary>
    public long MaxExpandedBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Largest ratio of uncompressed to compressed size an archive may declare.</summary>
    public int MaxCompressionRatio { get; set; } = 100;

    public int MaxArchiveEntries { get; set; } = 10_000;
}

/// <summary>Refuses settings that could never work, at startup.</summary>
public sealed class AssetOptionsValidator : IValidateOptions<AssetOptions>
{
    public ValidateOptionsResult Validate(string? name, AssetOptions options)
    {
        var errors = new List<string>();
        if (options.MaxPartBytes < options.MinPartBytes)
        {
            errors.Add("Assets:MaxPartBytes must not be smaller than Assets:MinPartBytes.");
        }

        if (options.MaxSimpleBytes > options.MaxObjectBytes)
        {
            errors.Add("Assets:MaxSimpleBytes must not be larger than Assets:MaxObjectBytes.");
        }

        if (options.MaxObjectBytes > options.MaxBytes)
        {
            errors.Add("Assets:MaxBytes must be at least Assets:MaxObjectBytes, or the largest object could never be stored.");
        }

        if (options.ScanRetryMaxDelay < options.ScanRetryBaseDelay)
        {
            errors.Add("Assets:ScanRetryMaxDelay must not be shorter than Assets:ScanRetryBaseDelay.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
