using System.Security.Cryptography;
using System.Text;

namespace Lateral.Cms.TestConsole;

public sealed record ChecksumCheck(string Algorithm, string Claimed, string Computed, bool Match);

/// <summary>
/// Checks a checksum the way any consumer of the API would: hash the payload text exactly as served and compare it
/// with the value named in the checksum itself (<c>sha256:...</c> or <c>md5:...</c>). It shares no code with the CMS,
/// so a match is independent confirmation that the documented form is reproducible.
/// </summary>
public static class ChecksumVerifier
{
    public static ChecksumCheck Check(string claimed, string payloadText)
    {
        var separator = claimed.IndexOf(':');
        var algorithm = separator < 0 ? "" : claimed[..separator];
        var bytes = Encoding.UTF8.GetBytes(payloadText);

        byte[]? hash = algorithm switch
        {
            "sha256" => SHA256.HashData(bytes),
            "md5" => MD5.HashData(bytes),
            _ => null,
        };

        var computed = hash is null ? "" : $"{algorithm}:{Convert.ToHexStringLower(hash)}";
        return new ChecksumCheck(algorithm, claimed, computed, hash is not null && computed == claimed);
    }
}
