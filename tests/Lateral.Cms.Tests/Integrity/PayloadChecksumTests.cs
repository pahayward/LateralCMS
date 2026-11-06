using System.ComponentModel.DataAnnotations;
using System.Text;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Integrity;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Tests.Integrity;

[Trait("req", "IN-07")]
public class PayloadChecksumTests
{
    private static HashPayloadChecksum With(string algorithm) =>
        new(Options.Create(new IngestionOptions { ChecksumAlgorithm = algorithm }));

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // Published test vectors for "abc".
    [Theory]
    [InlineData("sha256", "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("md5", "md5:900150983cd24fb0d6963f7d28e17f72")]
    public void Checksums_match_the_published_vectors_and_name_their_algorithm(string algorithm, string expected)
    {
        Assert.Equal(expected, With(algorithm).Compute(Bytes("abc")));
    }

    [Fact]
    public void The_default_algorithm_is_sha256()
    {
        Assert.Equal("sha256", new IngestionOptions().ChecksumAlgorithm);
        Assert.Equal("sha256", With("sha256").Algorithm);
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("md5")]
    public void A_checksum_verifies_against_the_payload_it_was_made_from(string algorithm)
    {
        var checksums = With(algorithm);
        var checksum = checksums.Compute(Bytes("""{"title":"Hello"}"""));

        Assert.True(checksums.Verify(checksum, Bytes("""{"title":"Hello"}""")));
        Assert.False(checksums.Verify(checksum, Bytes("""{"title":"Hellp"}""")));
    }

    [Fact]
    public void Old_checksums_stay_verifiable_after_the_configured_algorithm_changes()
    {
        var md5 = With("md5").Compute(Bytes("abc"));
        var sha = With("sha256").Compute(Bytes("abc"));

        // A service now configured for sha256 can still check a value written under md5, and vice versa.
        Assert.True(With("sha256").Verify(md5, Bytes("abc")));
        Assert.True(With("md5").Verify(sha, Bytes("abc")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nocolon")]
    [InlineData(":abc")]
    [InlineData("crc32:352441c2")]
    [InlineData("sha256:")]
    [InlineData("sha256:ba7816bf")]
    [InlineData("SHA256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void Malformed_unknown_or_wrong_checksums_never_verify(string checksum)
    {
        Assert.False(With("sha256").Verify(checksum, Bytes("abc")));
    }

    [Fact]
    public void An_unsupported_algorithm_is_refused_when_the_service_is_built()
    {
        Assert.Throws<ArgumentException>(() => With("crc32"));
    }

    [Theory]
    [InlineData("sha256", true)]
    [InlineData("md5", true)]
    [InlineData("crc32", false)]
    [InlineData("SHA256", false)]
    [InlineData("", false)]
    public void Only_supported_algorithms_pass_configuration_validation(string algorithm, bool valid)
    {
        var options = new IngestionOptions { ChecksumAlgorithm = algorithm };

        var ok = Validator.TryValidateObject(options, new ValidationContext(options), validationResults: null, validateAllProperties: true);

        Assert.Equal(valid, ok);
    }
}
