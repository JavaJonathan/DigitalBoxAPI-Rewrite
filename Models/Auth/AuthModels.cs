using System.ComponentModel.DataAnnotations;
using DigitalBoxApi.Entities;

namespace DigitalBoxApi.Models.Auth;

public class LoginRequestModel
{
    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public class LoginResponseModel
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public AuthUserModel User { get; set; } = new();
}

/// <summary>The logged-in identity, shared by the login response and <c>GET /api/auth/me</c>.</summary>
public class AuthUserModel
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = nameof(UserRole.User);

    public static AuthUserModel From(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        Role = u.Role.ToString()
    };
}
