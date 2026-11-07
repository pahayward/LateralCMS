using System.Security.Cryptography;
using System.Text;

namespace Lateral.Cms.Auth;

/// <summary>
/// Slow, salted password hashing (PBKDF2 with HMAC-SHA256). The stored text names its own iteration count, so the cost can be
/// raised later: a hash made with fewer iterations than the current setting is reported as stale and is replaced at the next
/// successful sign-in.
/// </summary>
public static class PasswordHasher
{
    public const string Scheme = "pbkdf2-sha256";
    public const int DefaultIterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Whether <paramref name="password"/> matches <paramref name="stored"/>. A malformed stored value never matches.</summary>
    public static bool Verify(string password, string stored, int currentIterations, out bool stale)
    {
        stale = false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var matches = CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations, expected.Length), expected);
        stale = matches && iterations < currentIterations;
        return matches;
    }

    /// <summary>
    /// A hash nobody's password produces, checked when the username does not exist so that "no such user" costs the same time as
    /// "wrong password" and the response time does not say which usernames are real.
    /// </summary>
    public static string Decoy(int iterations) => Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), iterations);

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, length);
}

/// <summary>What a password must be to be accepted, and a way to make one that is.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    // Length matters far more than composition rules; refusing the handful everybody tries first costs nothing.
    private static readonly string[] Common =
    [
        "password", "password1", "passw0rd", "123456789012", "qwertyuiopas", "letmein12345", "administrator", "welcome12345",
    ];

    /// <summary>Why the password is not acceptable, or null if it is.</summary>
    public static string? Problem(string? password, string username)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            return $"The password must be at least {MinLength} characters.";
        }

        if (password.Length > MaxLength)
        {
            return $"The password must be at most {MaxLength} characters.";
        }

        if (password.Trim().Length == 0)
        {
            return "The password cannot be only spaces.";
        }

        if (string.Equals(password, username, StringComparison.OrdinalIgnoreCase)
            || Common.Contains(password, StringComparer.OrdinalIgnoreCase))
        {
            return "That password is too easy to guess.";
        }

        return null;
    }

    // No 0/O/1/l/I, so it can be read out or typed from a screen without mistakes.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A random 20-character password in five groups, for a temporary or first-run password (about 114 bits).</summary>
    public static string Generate()
    {
        var chars = new char[24];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = i % 5 == 4 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
