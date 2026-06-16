using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/pr-events")]
[Authorize]
public class PrEventsController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/pr-events
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePrEventRequest req)
    {
        var ev = new PrEvent
        {
            UserId = Me,
            ExerciseId = req.ExerciseId,
            PerformedAt = req.PerformedAt.ToUniversalTime(),
            WeightKg = req.Weight,
            Reps = req.Reps,
            Kind = req.Kind,
        };
        db.PrEvents.Add(ev);
        await db.SaveChangesAsync();
        return Created($"/api/pr-events/{ev.Id}", new { id = ev.Id });
    }

    // GET /api/pr-events?exerciseId=12
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? exerciseId)
    {
        var q = db.PrEvents.Where(p => p.UserId == Me);
        if (exerciseId.HasValue) q = q.Where(p => p.ExerciseId == exerciseId);
        var results = await q.OrderByDescending(p => p.PerformedAt).ToListAsync();
        return Ok(results.Select(p => new
        {
            p.Id,
            p.ExerciseId,
            p.PerformedAt,
            weight = p.WeightKg,
            p.Reps,
            p.Kind
        }));
    }
}

public record CreatePrEventRequest(
    int ExerciseId, DateTime PerformedAt,
    double Weight, int Reps, string Kind);