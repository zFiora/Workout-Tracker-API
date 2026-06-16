using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/measurements")]
[Authorize]
public class MeasurementsController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/measurements
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMeasurementRequest req)
    {
        var m = new Measurement
        {
            UserId = Me,
            Date = req.Date.ToUniversalTime(),
            WeightKg = req.WeightKg,
        };
        db.Measurements.Add(m);
        await db.SaveChangesAsync();
        return Created($"/api/measurements/{m.Id}", new { m.Id, m.Date, m.WeightKg });
    }

    // GET /api/measurements?from=2025-01-01
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DateTime? from)
    {
        var q = db.Measurements.Where(m => m.UserId == Me);
        if (from.HasValue) q = q.Where(m => m.Date >= from.Value.ToUniversalTime());
        var results = await q.OrderBy(m => m.Date).ToListAsync();
        return Ok(results.Select(m => new { m.Id, m.Date, m.WeightKg }));
    }

    // DELETE /api/measurements/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var m = await db.Measurements
            .FirstOrDefaultAsync(m => m.Id == id && m.UserId == Me);
        if (m is null) return NotFound();
        db.Measurements.Remove(m);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreateMeasurementRequest(DateTime Date, double WeightKg);