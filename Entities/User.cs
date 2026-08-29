namespace DigitalBoxApi.Entities;

/// <summary>
/// An application login. Replaces the old single shared credential. Usernames only (no email);
/// passwords are admin-issued and never self-service. See <c>Controllers/UsersController</c>.
/// </summary>
public class User
{
    public Guid Id { get; set; }

    /// <summary>Login handle. Stored lower-cased; unique. Never changes.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Human name shown in the sidebar and stamped onto the order audit trail.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash in <c>Services/PasswordHasher</c> format.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>Deactivated users cannot log in and existing sessions are rejected immediately.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Rotated on every password reset and on deactivation. Baked into the JWT and re-checked
    /// per request (<c>Program.cs</c> <c>OnTokenValidated</c>) so those actions take effect at once.
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
