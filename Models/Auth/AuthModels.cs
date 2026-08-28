using System.ComponentModel.DataAnnotations;

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
    public string Username { get; set; } = string.Empty;
}

public class MeResponseModel
{
    public string Username { get; set; } = string.Empty;
}
