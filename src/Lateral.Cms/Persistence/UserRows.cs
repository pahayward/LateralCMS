namespace Lateral.Cms.Persistence;

/// <summary>
/// A person who signs in. The password is never stored, only a salted, slow hash of it. <see cref="SecurityStamp"/> changes
/// whenever the password or the disabled flag does, which ends every session and cached sign-in that was made before.
/// </summary>
public sealed class UserRow
{
    public Guid Id { get; set; }

    /// <summary>Unique without regard to case.</summary>
    public string Username { get; set; } = "";

    /// <summary><c>User</c> or <c>Admin</c>.</summary>
    public string Role { get; set; } = "";

    /// <summary><c>pbkdf2-sha256$iterations$salt$hash</c>.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>A password somebody else chose (a temporary one) must be replaced before anything else is allowed.</summary>
    public bool MustChangePassword { get; set; }

    public bool Disabled { get; set; }

    public Guid SecurityStamp { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime PasswordChangedAt { get; set; }

    public DateTime? LastSignInAt { get; set; }
}
