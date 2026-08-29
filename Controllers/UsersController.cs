using System.Security.Claims;
using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using DigitalBoxApi.Models.Users;
using DigitalBoxApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DigitalBoxApi.Controllers;

// Account administration. Admin-only. Passwords are always system-generated and shown once —
// there is no set-a-password field and no self-service. New accounts are always role
// UserRole.User; admins are seeded with the create-admin CLI command.
[ApiController]
[Route("api/users")]
[Authorize(Roles = nameof(UserRole.Admin))]
public class UsersController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordGenerator _passwords;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        ApplicationDbContext db,
        IPasswordGenerator passwords,
        ILogger<UsersController> logger)
    {
        _db = db;
        _passwords = passwords;
        _logger = logger;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserListItemModel>>> List(CancellationToken ct)
    {
        var users = await _db.Users
            .OrderByDescending(u => u.Role)
            .ThenBy(u => u.DisplayName)
            .Select(u => UserListItemModel.From(u))
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPost]
    public async Task<ActionResult<GeneratedPasswordModel>> Create(CreateUserRequestModel request, CancellationToken ct)
    {
        var username = UserService.NormalizeUsername(request.Username);
        if (await _db.Users.AnyAsync(u => u.Username == username, ct))
        {
            return Conflict(new { message = $"A user named \"{username}\" already exists." });
        }

        var password = _passwords.Generate();
        var user = UserService.NewUser(request.Username, request.DisplayName, UserRole.User, password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("User {Username} ({Id}) created by {ActorId}.",
            user.Username, user.Id, CurrentUserId);

        return CreatedAtAction(nameof(List), new GeneratedPasswordModel
        {
            User = UserListItemModel.From(user),
            GeneratedPassword = password
        });
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<ActionResult<GeneratedPasswordModel>> ResetPassword(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        var password = _passwords.Generate();
        user.PasswordHash = PasswordHasher.Hash(password);
        user.SecurityStamp = Guid.NewGuid(); // invalidates any existing session immediately
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Password for {Username} ({Id}) reset by {ActorId}.",
            user.Username, user.Id, CurrentUserId);

        return Ok(new GeneratedPasswordModel
        {
            User = UserListItemModel.From(user),
            GeneratedPassword = password
        });
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<ActionResult<UserListItemModel>> Deactivate(Guid id, CancellationToken ct)
    {
        if (id == CurrentUserId)
        {
            return BadRequest(new { message = "You cannot deactivate your own account." });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (user.IsActive)
        {
            user.IsActive = false;
            user.SecurityStamp = Guid.NewGuid(); // drop existing sessions immediately
            user.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("User {Username} ({Id}) deactivated by {ActorId}.",
                user.Username, user.Id, CurrentUserId);
        }

        return Ok(UserListItemModel.From(user));
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<ActionResult<UserListItemModel>> Activate(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (!user.IsActive)
        {
            user.IsActive = true;
            user.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("User {Username} ({Id}) reactivated by {ActorId}.",
                user.Username, user.Id, CurrentUserId);
        }

        return Ok(UserListItemModel.From(user));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserListItemModel>> Rename(Guid id, UpdateUserRequestModel request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        user.DisplayName = request.DisplayName.Trim();
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(UserListItemModel.From(user));
    }
}
