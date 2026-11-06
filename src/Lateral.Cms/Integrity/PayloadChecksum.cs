using System.Security.Cryptography;
using Lateral.Cms.Ingestion;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Integrity;

/// <summary>
/// Produces and checks the checksum stored alongside every payload.
/// </summary>
/// <remarks>
/// A checksum is a self-describing string, <c>"sha256:&lt;hex&gt;"</c> or <c>"md5:&lt;hex&gt;"</c>, so values written
/// under one algorithm stay checkable after the configured algorithm changes. It detects corruption and
/// lets a sender confirm what was stored. It is not a signature: anyone able to rewrite both the payload
/// and the checksum (e.g. with database write access) can make them agree.
/// </remarks>
public interface IPayloadChecksum
{
    /// <summary>Algorithm used for new checksums.</summary>
    string Algorithm { get; }

    /// <summary>Checksum of the payload bytes (UTF-8 of the canonical JSON text).</summary>
    string Compute(ReadOnlySpan<byte> payload);

    /// <summary>True if <paramref name="checksum"/> is well-formed and matches <paramref name="payload"/>.</summary>
    bool Verify(string checksum, ReadOnlySpan<byte> payload);
}

public sealed class HashPayloadChecksum : IPayloadChecksum
{
    public const string Sha256 = "sha256";
    public const string Md5 = "md5";

    public HashPayloadChecksum(IOptions<IngestionOptions> options)
    {
        Algorithm = options.Value.ChecksumAlgorithm;

        // Surfaces an unsupported algorithm, or one the platform forbids (MD5 under FIPS mode), at startup
        // rather than on the first event.
        _ = Compute(Algorithm, []);
    }

    public string Algorithm { get; }

    public string Compute(ReadOnlySpan<byte> payload) => Compute(Algorithm, payload);

    public bool Verify(string checksum, ReadOnlySpan<byte> payload)
    {
        var separator = checksum.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        var algorithm = checksum[..separator];
        if (algorithm is not (Sha256 or Md5))
        {
            return false;
        }

        // Checks against the algorithm the value names, not the configured one.
        return string.Equals(Compute(algorithm, payload), checksum, StringComparison.Ordinal);
    }

    public static string Compute(string algorithm, ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        var written = algorithm switch
        {
            Sha256 => SHA256.HashData(payload, hash),
            Md5 => MD5.HashData(payload, hash),
            _ => throw new ArgumentException($"Unsupported checksum algorithm '{algorithm}'.", nameof(algorithm)),
        };

        return string.Concat(algorithm, ":", Convert.ToHexStringLower(hash[..written]));
    }
}
