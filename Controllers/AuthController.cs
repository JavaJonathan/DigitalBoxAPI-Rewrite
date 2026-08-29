using System.Security.Claims;
using DigitalBoxApi.Data;
using DigitalBoxApi.Models.Auth;
using DigitalBoxApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DigitalBoxApi.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    /// <summary>Fixed delay on a failed login — invisible to a person, throttles scripted guessing.</summary>
    private static readonly TimeSpan FailureDelay = TimeSpan.FromMilliseconds(400);

    private readonly ApplicationDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly LoginThrottle _throttle;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ApplicationDbContext db,
        IJwtTokenService jwt,
        LoginThrottle throttle,
        ILogger<AuthController> logger)
    {
        _db = db;
        _jwt = jwt;
        _throttle = throttle;
        _logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponseModel>> Login(LoginRequestModel request, CancellationToken ct)
    {
        var clientKey = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (_throttle.IsLockedOut(clientKey))
        {
            _logger.LogWarning("Login blocked for {Client}: too many failed attempts.", clientKey);
            return StatusCode(StatusCodes.Status423Locked,
                new { message = "Too many failed login attempts. Try again in 15 minutes." });
        }

        var username = request.Username.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

        var ok = user is { IsActive: true } && PasswordHasher.Verify(request.Password, user.PasswordHash);
        if (!ok)
        {
            _throttle.RecordFailure(clientKey);
            _logger.LogWarning("Login failed for {Client}.", clientKey);
            await Task.Delay(FailureDelay, ct);
            return Unauthorized(new { message = "Invalid username or password." });
        }

        _throttle.RecordSuccess(clientKey);
        user!.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var (token, expiresAtUtc) = _jwt.CreateToken(user);
        return Ok(new LoginResponseModel
        {
            Token = token,
            ExpiresAtUtc = expiresAtUtc,
            User = AuthUserModel.From(user)
        });
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<AuthUserModel> Me() => Ok(new AuthUserModel
    {
        Id = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty,
        Username = User.FindFirstValue("preferred_username") ?? string.Empty,
        DisplayName = User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
        Role = User.FindFirstValue(ClaimTypes.Role) ?? nameof(Entities.UserRole.User)
    });
}
