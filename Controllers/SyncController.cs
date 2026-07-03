using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/sync")]
[Authorize]
public class SyncController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/sync/bootstrap?sessionDays=7 — templates + recent sessions + streak
    // in one round trip, for a cheap cold start.
    [HttpGet("bootstrap")]
    public async Task<IActionResult> Bootstrap([FromQuery] int sessionDays = 7)
    {
        var uid = Me;

        var templates = await db.Templates
            .Where(t => (t.UserId == uid || t.IsPublic) && t.DeletedAt == null)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();

        var cutoff = DateTime.UtcNow.AddDays(-sessionDays);
        var sessions = await db.WorkoutSessions
            .Where(s => s.UserId == uid && s.EndedAt >= cutoff)
            .OrderByDescending(s => s.EndedAt)
            .ToListAsync();

        var user = await db.Users.FindAsync(uid);

        return Ok(new BootstrapDto(
            templates.Select(TemplatesController.ToDto).ToList(),
            sessions.Select(WorkoutSessionsController.ToDto).ToList(),
            user?.CurrentStreak ?? 0,
            user?.BestStreak ?? 0,
            user?.LastWorkoutDate?.ToString("yyyy-MM-dd"),
            DateTime.UtcNow.ToString("o")));
    }
}

public record BootstrapDto(
    List<TemplateDto> Templates,
    List<WorkoutSessionDto> Sessions,
    int CurrentStreak,
    int BestStreak,
    string? LastWorkoutDate,
    string ServerTime);
