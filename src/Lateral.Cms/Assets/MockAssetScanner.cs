using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Assets;

public enum ScanVerdict
{
    Clean,

    /// <summary>A known-bad signature matched.</summary>
    Infected,

    /// <summary>Not a known signature, but something a careful reader would refuse: a disguised executable, active content, a bomb.</summary>
    Suspicious,

    /// <summary>The scanner could not decide. Treated like an exception: the asset stays quarantined.</summary>
    Error,
}

/// <param name="Key">The client's name for the asset.</param>
/// <param name="DeclaredType">What the sender claimed it is. Evidence, not truth.</param>
/// <param name="Size">Size in bytes.</param>
public sealed record AssetInfo(string Key, string DeclaredType, long Size);

public sealed record ScanResult(ScanVerdict Verdict, string? Signature, string? DetectedType, string ScannerName, string ScannerVersion);

/// <summary>
/// The seam for a content scanner. <see cref="MockAssetScanner"/> is a stand-in that proves the pipeline; a real engine (for
/// example ClamAV over <c>clamd</c> INSTREAM) is another implementation, with no change to upload, quarantine or download.
/// </summary>
public interface IAssetScanner
{
    string Name { get; }

    /// <summary>Changes when the rules change, so older verdicts can be found and re-queued.</summary>
    string Version { get; }

    /// <summary>Scans the bytes. Throw, or return <see cref="ScanVerdict.Error"/>, if it cannot decide: that is never read as clean.</summary>
    Task<ScanResult> ScanAsync(Stream content, AssetInfo info, CancellationToken cancellationToken);
}

/// <summary>
/// A deterministic, harmless stand-in for a scanner. It is **not** protection: it recognises a handful of obvious things so the
/// whole path (quarantine, verdict, wipe, retry, fail closed) can be built and tested. Rules: a made-up test signature (and, if
/// switched on, the EICAR test file); executables, also when disguised as another type; a declared type that the bytes
/// contradict; active content in SVG, HTML and PDF; archives that declare a decompression bomb; and, if switched on, markers
/// that simulate a scanner that errors or hangs.
/// </summary>
public sealed class MockAssetScanner(IOptions<AssetOptions> options) : IAssetScanner
{
    public const string TestSignatureText = "LATERAL-MOCK-VIRUS-TEST-FILE";
    public const string ErrorMarker = "MOCK-SCAN-ERROR";
    public const string TimeoutMarker = "MOCK-SCAN-TIMEOUT";

    // The industry test string, assembled from pieces so this source file is not itself flagged by antivirus.
    private static readonly string Eicar = string.Concat("X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-ANTIVIRUS-", "TEST-FILE!$H+H*");

    private enum Hit
    {
        TestSignature,
        Eicar,
        Error,
        Timeout,
        ActiveContent,
    }

    public string Name => "mock";

    public string Version => "mock-1";

    public async Task<ScanResult> ScanAsync(Stream content, AssetInfo info, CancellationToken cancellationToken)
    {
        var settings = options.Value.Mock;

        var head = new byte[8192];
        var headLength = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        var (detected, executable) = Detect(head.AsSpan(0, headLength));

        var patterns = new List<(byte[] Bytes, Hit Hit, string Label)> { (Lower(TestSignatureText), Hit.TestSignature, "Mock.TestSignature") };
        if (settings.DetectEicar)
        {
            patterns.Add((Lower(Eicar), Hit.Eicar, "Eicar-Test-Signature"));
        }

        if (settings.EnableFailureMarkers)
        {
            patterns.Add((Lower(ErrorMarker), Hit.Error, "error"));
            patterns.Add((Lower(TimeoutMarker), Hit.Timeout, "timeout"));
        }

        if (detected is "image/svg+xml" or "text/html")
        {
            foreach (var text in new[] { "<script", "javascript:", "onload=", "onerror=", "<foreignobject" })
            {
                patterns.Add((Lower(text), Hit.ActiveContent, "Mock.ActiveContent"));
            }
        }
        else if (detected == "application/pdf")
        {
            foreach (var text in new[] { "/javascript", "/openaction", "/launch" })
            {
                patterns.Add((Lower(text), Hit.ActiveContent, "Mock.ActiveContent"));
            }
        }

        var found = await SearchAsync(content, head.AsMemory(0, headLength), patterns, cancellationToken);
        switch (found?.Hit)
        {
            case Hit.TestSignature or Hit.Eicar:
                return Result(ScanVerdict.Infected, found.Value.Label, detected);
            case Hit.Error:
                throw new InvalidOperationException("The mock scanner was told to fail.");
            case Hit.Timeout:
                // A scanner that hangs. The caller's timeout ends this, and it counts as a failed attempt, never as clean.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                break;
        }

        if (executable)
        {
            var claimsExecutable = info.DeclaredType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
                                   && !info.DeclaredType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
            if (!claimsExecutable)
            {
                return Result(ScanVerdict.Suspicious, "Mock.ExecutableDisguised", detected);
            }

            if (!settings.AllowExecutables)
            {
                return Result(ScanVerdict.Infected, "Mock.Executable", detected);
            }
        }

        if ((info.DeclaredType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !detected.StartsWith("image/", StringComparison.Ordinal))
            || (info.DeclaredType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) && detected != "application/pdf"))
        {
            return Result(ScanVerdict.Suspicious, "Mock.TypeMismatch", detected);
        }

        if (found?.Hit == Hit.ActiveContent)
        {
            return Result(ScanVerdict.Suspicious, found.Value.Label, detected);
        }

        if (detected == "application/zip" && content.CanSeek)
        {
            if (CheckArchive(content, settings) is { } archiveSignature)
            {
                return Result(archiveSignature == "Mock.MalformedArchive" ? ScanVerdict.Suspicious : ScanVerdict.Infected, archiveSignature, detected);
            }
        }

        return Result(ScanVerdict.Clean, null, detected);
    }

    private ScanResult Result(ScanVerdict verdict, string? signature, string detected) => new(verdict, signature, detected, Name, Version);

    private static byte[] Lower(string text) => Encoding.ASCII.GetBytes(text.ToLowerInvariant());

    /// <summary>
    /// Looks for the patterns case-insensitively in the head (already read) and then the rest of the stream, in chunks, keeping
    /// an overlap so a pattern split across two chunks is still found. A definite hit returns at once; active content is only
    /// reported at the end, so it never hides a signature that comes later.
    /// </summary>
    private static async Task<(Hit Hit, string Label)?> SearchAsync(
        Stream content, ReadOnlyMemory<byte> head, List<(byte[] Bytes, Hit Hit, string Label)> patterns, CancellationToken cancellationToken)
    {
        const int ChunkSize = 64 * 1024;
        var overlap = patterns.Max(p => p.Bytes.Length) - 1;
        var window = new byte[overlap + ChunkSize];
        var carried = 0;
        (Hit, string)? activeContent = null;

        // Adds data after the carried-over tail, lower-cases it, searches, and keeps the new tail. Returns a definite hit.
        (Hit, string)? Feed(ReadOnlySpan<byte> data)
        {
            data.CopyTo(window.AsSpan(carried));
            var length = carried + data.Length;
            for (var i = carried; i < length; i++)
            {
                if (window[i] is >= (byte)'A' and <= (byte)'Z')
                {
                    window[i] += 32;
                }
            }

            var span = window.AsSpan(0, length);
            foreach (var (bytes, hit, label) in patterns)
            {
                if (span.IndexOf(bytes) < 0)
                {
                    continue;
                }

                if (hit != Hit.ActiveContent)
                {
                    return (hit, label);
                }

                activeContent ??= (hit, label);
            }

            carried = Math.Min(overlap, length);
            window.AsSpan(length - carried, carried).CopyTo(window);
            return null;
        }

        if (head.Length > 0 && Feed(head.Span) is { } inHead)
        {
            return inHead;
        }

        var chunk = new byte[ChunkSize];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (Feed(chunk.AsSpan(0, read)) is { } match)
            {
                return match;
            }
        }

        return activeContent;
    }

    private static bool StartsWith(ReadOnlySpan<byte> head, params byte[] magic) => head.Length >= magic.Length && head[..magic.Length].SequenceEqual(magic);

    /// <summary>What the bytes are, by looking at the start of them. The second value is true for executables.</summary>
    internal static (string Type, bool Executable) Detect(ReadOnlySpan<byte> head)
    {
        if (StartsWith(head, 0x4D, 0x5A) || StartsWith(head, 0x7F, 0x45, 0x4C, 0x46) || StartsWith(head, 0xFE, 0xED, 0xFA, 0xCE) || StartsWith(head, 0xFE, 0xED, 0xFA, 0xCF)
            || StartsWith(head, 0xCE, 0xFA, 0xED, 0xFE) || StartsWith(head, 0xCF, 0xFA, 0xED, 0xFE) || StartsWith(head, 0xCA, 0xFE, 0xBA, 0xBE))
        {
            return ("application/x-executable", true);
        }

        if (StartsWith(head, (byte)'#', (byte)'!'))
        {
            return ("text/x-script", true);
        }

        if (StartsWith(head, 0x89, 0x50, 0x4E, 0x47))
        {
            return ("image/png", false);
        }

        if (StartsWith(head, 0xFF, 0xD8, 0xFF))
        {
            return ("image/jpeg", false);
        }

        if (StartsWith(head, (byte)'G', (byte)'I', (byte)'F', (byte)'8'))
        {
            return ("image/gif", false);
        }

        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ("image/webp", false);
        }

        if (StartsWith(head, (byte)'%', (byte)'P', (byte)'D', (byte)'F'))
        {
            return ("application/pdf", false);
        }

        if (StartsWith(head, 0x50, 0x4B, 0x03, 0x04) || StartsWith(head, 0x50, 0x4B, 0x05, 0x06))
        {
            return ("application/zip", false);
        }

        if (head.IndexOf((byte)0) >= 0)
        {
            return ("application/octet-stream", false);
        }

        var text = Encoding.UTF8.GetString(head).ToLowerInvariant();
        if (text.Contains("<svg", StringComparison.Ordinal))
        {
            return ("image/svg+xml", false);
        }

        if (text.Contains("<!doctype html", StringComparison.Ordinal) || text.Contains("<html", StringComparison.Ordinal))
        {
            return ("text/html", false);
        }

        return text.TrimStart().StartsWith("<?xml", StringComparison.Ordinal) ? ("application/xml", false) : ("text/plain", false);
    }

    /// <summary>Reads only the archive's directory (never decompresses). Returns a signature if it declares a bomb or is malformed.</summary>
    private static string? CheckArchive(Stream content, MockScannerOptions settings)
    {
        try
        {
            content.Position = 0;
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > settings.MaxArchiveEntries)
            {
                return "Mock.ArchiveBomb";
            }

            long expanded = 0;
            long compressed = 0;
            foreach (var entry in archive.Entries)
            {
                expanded += entry.Length;
                compressed += entry.CompressedLength;
            }

            if (expanded > settings.MaxExpandedBytes || (compressed > 0 && expanded / compressed > settings.MaxCompressionRatio))
            {
                return "Mock.ArchiveBomb";
            }

            return null;
        }
        catch (InvalidDataException)
        {
            return "Mock.MalformedArchive";
        }
    }
}
