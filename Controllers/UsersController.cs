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
public class UsersController(AppDbContext db) : ControllerBase
{
    private const long MaxAvatarBytes = 2 * 1024 * 1024;

    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/users/me
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Me);
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

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
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

        if (req.TimeZoneId is not null)
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(req.TimeZoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return BadRequest(new { message = "Invalid time zone." });
            }
            user.TimeZoneId = req.TimeZoneId;
        }

        await db.SaveChangesAsync();
        return Ok(ToDto(user));
    }

    // GET /api/users/me/streak
    [HttpGet("me/streak")]
    public async Task<IActionResult> GetStreak()
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Me);
        if (user is null) return NotFound();

        return Ok(new StreakDto(
            StreakCalculator.EffectiveCurrentStreak(user.CurrentStreak, user.LastQualifyingWorkoutAt, DateTime.UtcNow),
            user.BestStreak,
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

    // DELETE /api/users/me — immediate, irreversible. Requires the current password
    // so a stolen/left-open session can't destroy the account without the owner's say-so.
    [HttpDelete("me")]
    public async Task<IActionResult> DeleteMe([FromBody] DeleteAccountRequest req)
    {
        var uid = Me;
        var user = await db.Users.FindAsync(uid);
        if (user is null) return NotFound();

        if (string.IsNullOrEmpty(req.Password) || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return BadRequest(new { message = "Incorrect password." });

        // Everything else this user owns cascades on delete (sessions, templates,
        // measurements, macro-profile, PR events, exercise notes, friendships, reset
        // tokens) — EXCEPT SharedTemplate (Owner/SharedWithUser/Template are all
        // Restrict, to stop a share from silently vanishing) and SavedTemplate's link
        // to SharedTemplate (same reason). Those have to be cleared by hand first, or
        // the cascade throws a foreign-key violation partway through.
        await using var tx = await db.Database.BeginTransactionAsync();

        var ownTemplateIds = await db.Templates
            .Where(t => t.UserId == uid)
            .Select(t => t.Id)
            .ToListAsync();

        var blockingShareIds = await db.SharedTemplates
            .Where(s => s.OwnerUserId == uid || s.SharedWithUserId == uid || ownTemplateIds.Contains(s.TemplateId))
            .Select(s => s.Id)
            .ToListAsync();

        if (blockingShareIds.Count > 0)
        {
            // Anyone's saved copy of a share being removed — not just this user's own.
            var dependentSaves = await db.SavedTemplates
                .Where(s => blockingShareIds.Contains(s.SharedTemplateId))
                .ToListAsync();
            db.SavedTemplates.RemoveRange(dependentSaves);
            await db.SaveChangesAsync();

            var shares = await db.SharedTemplates
                .Where(s => blockingShareIds.Contains(s.Id))
                .ToListAsync();
            db.SharedTemplates.RemoveRange(shares);
            await db.SaveChangesAsync();
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync();

        await tx.CommitAsync();

        return Ok(new { message = "Account deleted." });
    }

    private static UserDto ToDto(User u) => new(
        u.Id.ToString(), u.Email, u.Username, u.DisplayName,
        u.AvatarBase64, u.AvatarContentType,
        StreakCalculator.EffectiveCurrentStreak(u.CurrentStreak, u.LastQualifyingWorkoutAt, DateTime.UtcNow),
        u.BestStreak,
        u.LastWorkoutDate?.ToString("yyyy-MM-dd"),
        u.TimeZoneId);

    private static PublicUserDto ToPublicDto(User u) => new(
        u.Id.ToString(), u.Username, u.DisplayName,
        u.AvatarBase64, u.AvatarContentType,
        StreakCalculator.EffectiveCurrentStreak(u.CurrentStreak, u.LastQualifyingWorkoutAt, DateTime.UtcNow),
        u.BestStreak);
}

public record UpdateUserRequest(
    string? DisplayName, string? Username, string? TimeZoneId = null);

public record DeleteAccountRequest(string Password);

public record UserDto(
    string Id, string Email, string Username,
    string? DisplayName, string? AvatarBase64, string? AvatarContentType,
    int CurrentStreak, int BestStreak,
    string? LastWorkoutDate, string? TimeZoneId = null);

public record PublicUserDto(
    string Id, string Username,
    string? DisplayName, string? AvatarBase64, string? AvatarContentType,
    int CurrentStreak, int BestStreak);

public record StreakDto(
    int CurrentStreak, int BestStreak, string? LastWorkoutDate);