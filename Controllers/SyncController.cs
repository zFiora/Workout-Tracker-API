using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/sync")]
[Authorize]
public class SyncController(IDbContextFactory<AppDbContext> dbFactory) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/sync/bootstrap?sessionDays=7 — templates + recent sessions + streak,
    // for a cheap cold start. The three reads are independent, so they run concurrently
    // (each against its own short-lived DbContext, since one context can't run
    // overlapping operations) instead of paying for three sequential DB round trips.
    [HttpGet("bootstrap")]
    public async Task<IActionResult> Bootstrap([FromQuery] int sessionDays = 7)
    {
        var uid = Me;
        var cutoff = DateTime.UtcNow.AddDays(-sessionDays);

        var templatesTask = LoadTemplatesAsync(uid);
        var sessionsTask = LoadSessionsAsync(uid, cutoff);
        var userTask = LoadUserAsync(uid);

        await Task.WhenAll(templatesTask, sessionsTask, userTask);

        var templates = await templatesTask;
        var sessions = await sessionsTask;
        var user = await userTask;

        return Ok(new BootstrapDto(
            templates.Select(TemplatesController.ToDto).ToList(),
            sessions.Select(WorkoutSessionsController.ToDto).ToList(),
            user?.CurrentStreak ?? 0,
            user?.BestStreak ?? 0,
            user?.LastWorkoutDate?.ToString("yyyy-MM-dd"),
            DateTime.UtcNow.ToString("o")));
    }

    private async Task<List<Models.Template>> LoadTemplatesAsync(Guid uid)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        return await ctx.Templates
            .AsNoTracking()
            .Where(t => (t.UserId == uid || t.IsPublic) && t.DeletedAt == null)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();
    }

    private async Task<List<Models.WorkoutSession>> LoadSessionsAsync(Guid uid, DateTime cutoff)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        return await ctx.WorkoutSessions
            .AsNoTracking()
            .Where(s => s.UserId == uid && s.EndedAt >= cutoff)
            .OrderByDescending(s => s.EndedAt)
            .ToListAsync();
    }

    private async Task<Models.User?> LoadUserAsync(Guid uid)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        return await ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid);
    }
}

public record BootstrapDto(
    List<TemplateDto> Templates,
    List<WorkoutSessionDto> Sessions,
    int CurrentStreak,
    int BestStreak,
    string? LastWorkoutDate,
    string ServerTime);
