using DigitalBoxApi.Entities;

namespace DigitalBoxApi.Services;

// Shared account helpers used by UsersController and the create-admin CLI.
public static class UserService
{
    public static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    // Builds a new, active user with the password already hashed. Timestamps set to now.
    public static User NewUser(string username, string displayName, UserRole role, string plainPassword)
    {
        var now = DateTime.UtcNow;
        return new User
        {
            Id = Guid.NewGuid(),
            Username = NormalizeUsername(username),
            DisplayName = displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(plainPassword),
            Role = role,
            IsActive = true,
            SecurityStamp = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
