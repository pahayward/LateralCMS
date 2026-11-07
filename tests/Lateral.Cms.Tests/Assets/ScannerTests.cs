using System.IO.Compression;
using System.Text;
using Lateral.Cms.Assets;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Tests.Assets;

/// <summary>
/// The mock scanner's rules, in memory (nothing touches the disk, so antivirus on a developer machine has nothing to quarantine,
/// not even the EICAR string, which is assembled at run time). These show the scanner is a believable stand-in; they are not
/// claims about real malware detection.
/// </summary>
[Trait("req", "SC-01")]
public class ScannerTests
{
    private static MockAssetScanner Scanner(Action<MockScannerOptions>? configure = null)
    {
        var options = new AssetOptions();
        configure?.Invoke(options.Mock);
        return new MockAssetScanner(Options.Create(options));
    }

    private static Task<ScanResult> ScanAsync(byte[] bytes, string declared = "application/octet-stream", Action<MockScannerOptions>? configure = null, CancellationToken ct = default) =>
        Scanner(configure).ScanAsync(new MemoryStream(bytes), new AssetInfo("f", declared, bytes.Length), ct);

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Png(int length = 200) => AssetFactory.Png(length);

    // ---- clean things stay clean ---------------------------------------------------------------------------

    [Theory]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    public async Task An_ordinary_image_is_clean_and_is_identified_by_its_bytes_not_by_the_claim(string declared)
    {
        var result = await ScanAsync(Png(5000), declared);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal("image/png", result.DetectedType);
        Assert.Equal("mock", result.ScannerName);
        Assert.Equal("mock-1", result.ScannerVersion);
    }

    [Theory]
    [InlineData("hello world", "text/plain")]
    [InlineData("<?xml version=\"1.0\"?><a/>", "application/xml")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect/></svg>", "image/svg+xml")]
    [InlineData("<!doctype html><html><body>hi</body></html>", "text/html")]
    public async Task Plain_text_and_markup_without_active_content_are_clean(string content, string expectedType)
    {
        var result = await ScanAsync(Text(content), expectedType);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal(expectedType, result.DetectedType);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
    [InlineData(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', (byte)'1' }, "application/pdf")]
    public async Task Common_types_are_recognised_by_their_magic_bytes(byte[] head, string expectedType)
    {
        var result = await ScanAsync(head, expectedType);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal(expectedType, result.DetectedType);
    }

    // ---- signatures ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_mock_test_signature_is_infected_wherever_it_appears_even_in_the_wrong_case()
    {
        var content = Png(4000).Concat(Text("..." + MockAssetScanner.TestSignatureText.ToLowerInvariant() + "...")).ToArray();

        var result = await ScanAsync(content, "image/png");

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal("Mock.TestSignature", result.Signature);
    }

    [Fact]
    public async Task A_signature_split_across_the_scanners_read_chunks_is_still_found()
    {
        // Pad so the signature straddles the 64 KiB chunk boundary used while reading.
        var signature = MockAssetScanner.TestSignatureText;
        var content = Text(new string(' ', 8192 + 65536 - 10) + signature);

        var result = await ScanAsync(content, "text/plain");

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
    }

    [Fact]
    public async Task The_eicar_test_file_is_infected_only_when_that_is_switched_on()
    {
        // Built from pieces here as in the scanner, so no file in this repository contains the whole string.
        var eicar = Text(string.Concat("X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-ANTIVIRUS-", "TEST-FILE!$H+H*"));

        var on = await ScanAsync(eicar, "text/plain", o => o.DetectEicar = true);
        var off = await ScanAsync(eicar, "text/plain");

        Assert.Equal(ScanVerdict.Infected, on.Verdict);
        Assert.Equal("Eicar-Test-Signature", on.Signature);
        Assert.Equal(ScanVerdict.Clean, off.Verdict);
    }

    // ---- executables, lies about type ----------------------------------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1 })]
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 7, 0 })]
    [InlineData(new byte[] { (byte)'#', (byte)'!', (byte)'/', (byte)'b', (byte)'i', (byte)'n' })]
    public async Task An_executable_dressed_up_as_an_image_is_suspicious(byte[] head)
    {
        var result = await ScanAsync(head.Concat(new byte[300]).ToArray(), "image/png");

        Assert.Equal(ScanVerdict.Suspicious, result.Verdict);
        Assert.Equal("Mock.ExecutableDisguised", result.Signature);
    }

    [Fact]
    public async Task A_bare_executable_is_infected_unless_executables_are_allowed()
    {
        var exe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 1, 2, 3 };

        var blocked = await ScanAsync(exe, "application/x-msdownload");
        var allowed = await ScanAsync(exe, "application/x-msdownload", o => o.AllowExecutables = true);

        Assert.Equal(ScanVerdict.Infected, blocked.Verdict);
        Assert.Equal("Mock.Executable", blocked.Signature);
        Assert.Equal(ScanVerdict.Clean, allowed.Verdict);
    }

    [Theory]
    [InlineData("image/png", "just some text")]
    [InlineData("application/pdf", "not a pdf at all")]
    public async Task A_declared_type_that_the_bytes_contradict_is_suspicious(string declared, string content)
    {
        var result = await ScanAsync(Text(content), declared);

        Assert.Equal(ScanVerdict.Suspicious, result.Verdict);
        Assert.Equal("Mock.TypeMismatch", result.Signature);
    }

    // ---- active content ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' onload='steal()'></svg>")]
    [InlineData("<svg><a href='JavaScript:alert(1)'><text>x</text></a></svg>")]
    [InlineData("<svg><foreignObject><div/></foreignObject></svg>")]
    [InlineData("<!DOCTYPE html><html><img src=x onerror=alert(1)></html>")]
    public async Task Script_in_svg_or_html_is_suspicious(string content)
    {
        var result = await ScanAsync(Text(content), "text/html");

        Assert.Equal(ScanVerdict.Suspicious, result.Verdict);
        Assert.Equal("Mock.ActiveContent", result.Signature);
    }

    [Theory]
    [InlineData("%PDF-1.7 1 0 obj << /OpenAction << /S /JavaScript /JS (app.alert(1)) >> >>")]
    [InlineData("%PDF-1.7 1 0 obj << /Launch /Win >>")]
    public async Task Script_or_launch_actions_in_a_pdf_are_suspicious(string content)
    {
        var result = await ScanAsync(Text(content), "application/pdf");

        Assert.Equal(ScanVerdict.Suspicious, result.Verdict);
        Assert.Equal("Mock.ActiveContent", result.Signature);
    }

    [Fact]
    public async Task The_word_script_in_plain_text_is_not_active_content()
    {
        var result = await ScanAsync(Text("a <script> tag in a note about web pages"), "text/plain");

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
    }

    [Fact]
    public async Task A_signature_wins_over_active_content_found_earlier()
    {
        var content = Text("<svg><script/></svg>" + new string(' ', 70_000) + MockAssetScanner.TestSignatureText);

        var result = await ScanAsync(content, "image/svg+xml");

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
    }

    // ---- archives ------------------------------------------------------------------------------------------

    private static byte[] Zip(Action<ZipArchive> fill)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            fill(archive);
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task An_ordinary_archive_is_clean()
    {
        var zip = Zip(a =>
        {
            using var writer = new StreamWriter(a.CreateEntry("note.txt").Open());
            writer.Write("a few words of text that do not compress very much at all");
        });

        var result = await ScanAsync(zip, "application/zip");

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal("application/zip", result.DetectedType);
    }

    [Fact]
    public async Task An_archive_that_declares_a_huge_expansion_is_a_bomb_judged_from_its_directory_alone()
    {
        var zip = Zip(a =>
        {
            using var entry = a.CreateEntry("zeros.bin", CompressionLevel.SmallestSize).Open();
            entry.Write(new byte[8 * 1024 * 1024]);
        });

        var result = await ScanAsync(zip, "application/zip");

        Assert.True(zip.Length < 100_000, "the test archive should be tiny");
        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal("Mock.ArchiveBomb", result.Signature);
    }

    [Fact]
    public async Task An_archive_with_too_many_entries_is_a_bomb()
    {
        var zip = Zip(a =>
        {
            for (var i = 0; i < 30; i++)
            {
                a.CreateEntry($"e{i}.txt").Open().Dispose();
            }
        });

        var result = await ScanAsync(zip, "application/zip", o => o.MaxArchiveEntries = 10);

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
    }

    [Fact]
    public async Task A_file_that_claims_to_be_an_archive_but_is_not_is_suspicious()
    {
        var result = await ScanAsync([0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8], "application/zip");

        Assert.Equal(ScanVerdict.Suspicious, result.Verdict);
        Assert.Equal("Mock.MalformedArchive", result.Signature);
    }

    // ---- simulated scanner failure --------------------------------------------------------------------------

    [Fact]
    public async Task The_error_marker_makes_the_scanner_throw_only_when_failure_markers_are_enabled()
    {
        var content = Text("harmless " + MockAssetScanner.ErrorMarker);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanAsync(content, "text/plain", o => o.EnableFailureMarkers = true));
        Assert.Equal(ScanVerdict.Clean, (await ScanAsync(content, "text/plain")).Verdict);
    }

    [Fact]
    public async Task The_timeout_marker_makes_the_scanner_hang_until_it_is_cancelled()
    {
        var content = Text("harmless " + MockAssetScanner.TimeoutMarker);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScanAsync(content, "text/plain", o => o.EnableFailureMarkers = true, cts.Token));
    }

    [Fact]
    public void The_scanner_names_a_version_so_older_verdicts_can_be_found()
    {
        Assert.Equal("mock-1", Scanner().Version);
    }
}
