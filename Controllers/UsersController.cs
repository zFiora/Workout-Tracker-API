using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;
using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController(AppDbContext db, CloudinaryService cloudinary) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/users/me
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var user = await db.Users.FindAsync(Me);
        return user is null ? NotFound() : Ok(ToDto(user));
    }

    // GET /api/users/{id} — public profile (must be a friend)
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var uid = Me;

        var areFriends = await db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == uid && f.AddresseeId == id) ||
             (f.RequesterId == id && f.AddresseeId == uid)));

        if (!areFriends && uid != id)
            return Forbid();

        var user = await db.Users.FindAsync(id);
        return user is null ? NotFound() : Ok(ToPublicDto(user));
    }

    // PATCH /api/users/me
    [HttpPatch("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateUserRequest req)
    {
        var user = await db.Users.FindAsync(Me);
        if (user is null) return NotFound();

        if (req.DisplayName is not null)     user.DisplayName    = req.DisplayName;
        if (req.Username is not null)        user.Username       = req.Username;
        if (req.CurrentStreak.HasValue)      user.CurrentStreak  = req.CurrentStreak.Value;
        if (req.BestStreak.HasValue)         user.BestStreak     = req.BestStreak.Value;
        if (req.LastWorkoutDate is not null)
            user.LastWorkoutDate = DateTime.Parse(req.LastWorkoutDate).ToUniversalTime();

        await db.SaveChangesAsync();
        return Ok(ToDto(user));
    }

    // PATCH /api/users/me/avatar
    [HttpPatch("me/avatar")]
    public async Task<IActionResult> UpdateAvatar(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "No file provided." });

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowedTypes.Contains(file.ContentType.ToLower()))
            return BadRequest(new { message = "Only JPEG, PNG and WebP are allowed." });

        if (file.Length > 5 * 1024 * 1024)
            return BadRequest(new { message = "Image must be under 5MB." });

        var user = await db.Users.FindAsync(Me);
        if (user is null) return NotFound();

        var url = await cloudinary.UploadAvatarAsync(file, Me.ToString());
        user.AvatarUrl = url;
        await db.SaveChangesAsync();

        return Ok(new { avatarUrl = url });
    }

    private static UserDto ToDto(User u) => new(
        u.Id.ToString(), u.Email, u.Username, u.DisplayName,
        u.AvatarUrl, u.CurrentStreak, u.BestStreak,
        u.LastWorkoutDate?.ToString("yyyy-MM-dd"));

    private static PublicUserDto ToPublicDto(User u) => new(
        u.Id.ToString(), u.Username, u.DisplayName,
        u.AvatarUrl, u.CurrentStreak, u.BestStreak);
}

public record UpdateUserRequest(
    string? DisplayName, string? Username,
    int? CurrentStreak, int? BestStreak,
    string? LastWorkoutDate);

public record UserDto(
    string Id, string Email, string Username,
    string? DisplayName, string? AvatarUrl,
    int CurrentStreak, int BestStreak,
    string? LastWorkoutDate);

public record PublicUserDto(
    string Id, string Username,
    string? DisplayName, string? AvatarUrl,
    int CurrentStreak, int BestStreak);