using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/workouts")]
[Authorize]
public class WorkoutsController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/workouts
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkoutRequest req)
    {
        var exists = await db.WorkoutEntries.AnyAsync(w =>
            w.UserId == Me &&
            w.TemplateId == req.TemplateId &&
            w.StartedAt == req.StartedAt.ToUniversalTime());

        if (exists) return Ok(new { message = "Already synced." });

        var entry = new WorkoutEntry
        {
            UserId       = Me,
            TemplateId   = req.TemplateId,
            TemplateName = req.TemplateName,
            TemplateIcon = req.TemplateIcon,
            StartedAt    = req.StartedAt.ToUniversalTime(),
            EndedAt      = req.EndedAt.ToUniversalTime(),
            DurationMs   = req.DurationMs,
            Logs         = JsonDocument.Parse(JsonSerializer.Serialize(req.Logs)),
        };

        db.WorkoutEntries.Add(entry);
        await db.SaveChangesAsync();
        return Created($"/api/workouts/{entry.Id}", new { id = entry.Id });
    }

    // GET /api/workouts?page=1&perPage=30
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int perPage = 30)
    {
        var skip    = (page - 1) * perPage;
        var entries = await db.WorkoutEntries
            .Where(w => w.UserId == Me)
            .OrderByDescending(w => w.StartedAt)
            .Skip(skip)
            .Take(perPage)
            .ToListAsync();

        var total = await db.WorkoutEntries.CountAsync(w => w.UserId == Me);

        return Ok(new
        {
            page, perPage, totalItems = total,
            items = entries.Select(WorkoutDto.From)
        });
    }

    // GET /api/workouts/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var entry = await db.WorkoutEntries
            .FirstOrDefaultAsync(w => w.Id == id && w.UserId == Me);
        return entry is null ? NotFound() : Ok(WorkoutDto.From(entry));
    }

    // DELETE /api/workouts/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var entry = await db.WorkoutEntries
            .FirstOrDefaultAsync(w => w.Id == id && w.UserId == Me);
        if (entry is null) return NotFound();
        db.WorkoutEntries.Remove(entry);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreateWorkoutRequest(
    string TemplateId, string TemplateName, string TemplateIcon,
    DateTime StartedAt, DateTime EndedAt, long DurationMs,
    List<JsonElement> Logs);

public record WorkoutDto(
    Guid Id, string TemplateId, string TemplateName, string TemplateIcon,
    DateTime StartedAt, DateTime EndedAt, long DurationMs, JsonElement Logs)
{
    public static WorkoutDto From(WorkoutEntry w) => new(
        w.Id, w.TemplateId, w.TemplateName, w.TemplateIcon,
        w.StartedAt, w.EndedAt, w.DurationMs,
        w.Logs.RootElement.Clone());
}