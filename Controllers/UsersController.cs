using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController(AppDbContext db) : ControllerBase
{
    private const long MaxAvatarBytes = 2 * 1024 * 1024;

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

        await db.SaveChangesAsync();
        return Ok(ToDto(user));
    }

    // GET /api/users/me/streak
    [HttpGet("me/streak")]
    public async Task<IActionResult> GetStreak()
    {
        var user = await db.Users.FindAsync(Me);
        if (user is null) return NotFound();

        return Ok(new StreakDto(
            user.CurrentStreak, user.BestStreak,
            user.LastWorkoutDate?.ToString("yyyy-MM-dd")));
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

        if (file.Length > MaxAvatarBytes)
            return BadRequest(new { message = "Image must be under 2MB." });

        var user = await db.Users.FindAsync(Me);
        if (user is null) return NotFound();

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        user.AvatarBase64 = Convert.ToBase64String(stream.ToArray());
        user.AvatarContentType = file.ContentType.ToLower();

        await db.SaveChangesAsync();

        return Ok(new { avatarBase64 = user.AvatarBase64, avatarContentType = user.AvatarContentType });
    }

    private static UserDto ToDto(User u) => new(
        u.Id.ToString(), u.Email, u.Username, u.DisplayName,
        u.AvatarBase64, u.AvatarContentType, u.CurrentStreak, u.BestStreak,
        u.LastWorkoutDate?.ToString("yyyy-MM-dd"));

    private static PublicUserDto ToPublicDto(User u) => new(
        u.Id.ToString(), u.Username, u.DisplayName,
        u.AvatarBase64, u.AvatarContentType, u.CurrentStreak, u.BestStreak);
}

public record UpdateUserRequest(
    string? DisplayName, string? Username);

public record UserDto(
    string Id, string Email, string Username,
    string? DisplayName, string? AvatarBase64, string? AvatarContentType,
    int CurrentStreak, int BestStreak,
    string? LastWorkoutDate);

public record PublicUserDto(
    string Id, string Username,
    string? DisplayName, string? AvatarBase64, string? AvatarContentType,
    int CurrentStreak, int BestStreak);

public record StreakDto(
    int CurrentStreak, int BestStreak, string? LastWorkoutDate);