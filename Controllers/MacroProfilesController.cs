using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/macro-profile")]
[Authorize]
public class MacroProfilesController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/macro-profile
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var p = await db.MacroProfiles.FirstOrDefaultAsync(m => m.UserId == Me);
        return p is null ? NotFound() : Ok(MacroDto.From(p));
    }

    // PUT /api/macro-profile
    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] MacroRequest req)
    {
        var p = await db.MacroProfiles.FirstOrDefaultAsync(m => m.UserId == Me);
        if (p is null)
        {
            p = new MacroProfile { UserId = Me };
            db.MacroProfiles.Add(p);
        }
        p.IsMale = req.IsMale;
        p.Age = req.Age;
        p.ActivityFactor = req.ActivityFactor;
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(MacroDto.From(p));
    }
}

public record MacroRequest(bool IsMale, int Age, double ActivityFactor);
public record MacroDto(Guid Id, bool IsMale, int Age, double ActivityFactor, DateTime UpdatedAt)
{
    public static MacroDto From(MacroProfile p) =>
        new(p.Id, p.IsMale, p.Age, p.ActivityFactor, p.UpdatedAt);
}