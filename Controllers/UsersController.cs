using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkoutTrackerAPI.Data;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/users/me
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var user = await db.Users.FindAsync(Me);
        return user is null ? NotFound() : Ok(UserDto.From(user));
    }

    // PATCH /api/users/me
    [HttpPatch("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateUserRequest req)
    {
        var user = await db.Users.FindAsync(Me);
        if (user is null) return NotFound();

        if (req.DisplayName is not null)    user.DisplayName    = req.DisplayName;
        if (req.AvatarUrl is not null)      user.AvatarUrl      = req.AvatarUrl;
        if (req.CurrentStreak.HasValue)     user.CurrentStreak  = req.CurrentStreak.Value;
        if (req.BestStreak.HasValue)        user.BestStreak     = req.BestStreak.Value;
        if (req.LastWorkoutDate.HasValue)   user.LastWorkoutDate = req.LastWorkoutDate.Value.ToUniversalTime();

        await db.SaveChangesAsync();
        return Ok(UserDto.From(user));
    }
}

public record UpdateUserRequest(
    string? DisplayName, string? AvatarUrl,
    int? CurrentStreak, int? BestStreak, DateTime? LastWorkoutDate);