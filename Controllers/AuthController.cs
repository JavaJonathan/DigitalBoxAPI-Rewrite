using System.Security.Claims;
using DigitalBoxApi.Models.Auth;
using DigitalBoxApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBoxApi.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IJwtTokenService _jwt;
    private readonly LoginThrottle _throttle;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IConfiguration configuration,
        IJwtTokenService jwt,
        LoginThrottle throttle,
        ILogger<AuthController> logger)
    {
        _configuration = configuration;
        _jwt = jwt;
        _throttle = throttle;
        _logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public ActionResult<LoginResponseModel> Login(LoginRequestModel request)
    {
        var clientKey = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (_throttle.IsLockedOut(clientKey))
        {
            _logger.LogWarning("Login blocked for {Client}: too many failed attempts.", clientKey);
            return StatusCode(StatusCodes.Status423Locked,
                new { message = "Too many failed login attempts. Try again in 15 minutes." });
        }

        var expectedUsername = _configuration["Auth:Username"];
        var expectedHash = _configuration["Auth:PasswordHash"];

        if (string.IsNullOrWhiteSpace(expectedUsername) || string.IsNullOrWhiteSpace(expectedHash))
        {
            _logger.LogError("Auth:Username / Auth:PasswordHash are not configured.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Login is not configured on the server." });
        }

        var ok = string.Equals(request.Username, expectedUsername, StringComparison.Ordinal)
                 && PasswordHasher.Verify(request.Password, expectedHash);

        if (!ok)
        {
            _throttle.RecordFailure(clientKey);
            _logger.LogWarning("Login failed for {Client}.", clientKey);
            return Unauthorized(new { message = "Invalid username or password." });
        }

        _throttle.RecordSuccess(clientKey);
        var (token, expiresAtUtc) = _jwt.CreateToken(expectedUsername);

        return Ok(new LoginResponseModel
        {
            Token = token,
            ExpiresAtUtc = expiresAtUtc,
            Username = expectedUsername
        });
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<MeResponseModel> Me() => Ok(new MeResponseModel
    {
        Username = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? string.Empty
    });
}
