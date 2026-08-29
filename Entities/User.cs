namespace DigitalBoxApi.Entities;

// An application login. Replaces the old single shared credential. Usernames only (no email);
// passwords are admin-issued and never self-service. See Controllers/UsersController.
public class User
{
    public Guid Id { get; set; }

    // Login handle. Stored lower-cased; unique. Never changes.
    public string Username { get; set; } = string.Empty;

    // Human name shown in the sidebar and stamped onto the order audit trail.
    public string DisplayName { get; set; } = string.Empty;

    // PBKDF2 hash in Services/PasswordHasher format.
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    // Deactivated users cannot log in and existing sessions are rejected immediately.
    public bool IsActive { get; set; } = true;

    // Rotated on every password reset and on deactivation. Baked into the JWT and re-checked
    // per request (Program.cs OnTokenValidated) so those actions take effect at once.
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
