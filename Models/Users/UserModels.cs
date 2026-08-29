using System.ComponentModel.DataAnnotations;
using DigitalBoxApi.Entities;

namespace DigitalBoxApi.Models.Users;

public class UserListItemModel
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = nameof(UserRole.User);
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public static UserListItemModel From(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        Role = u.Role.ToString(),
        IsActive = u.IsActive,
        CreatedAt = u.CreatedAt,
        LastLoginAt = u.LastLoginAt
    };
}

public class CreateUserRequestModel
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9._-]{2,64}$",
        ErrorMessage = "Username must be 2–64 characters: letters, digits, dot, dash or underscore.")]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;
}

public class UpdateUserRequestModel
{
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>Returned once, on create and on password reset. The passphrase is never stored or logged.</summary>
public class GeneratedPasswordModel
{
    public UserListItemModel User { get; set; } = new();
    public string GeneratedPassword { get; set; } = string.Empty;
}
