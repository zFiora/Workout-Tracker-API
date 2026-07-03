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
        var p = await db.MacroProfiles
            .FirstOrDefaultAsync(m => m.UserId == Me);
        return p is null ? NotFound() : Ok(ToDto(p));
    }

    // PUT /api/macro-profile — upsert
    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] MacroRequest req)
    {
        var p = await db.MacroProfiles
            .FirstOrDefaultAsync(m => m.UserId == Me);

        if (p is null)
        {
            p = new MacroProfile { UserId = Me };
            db.MacroProfiles.Add(p);
        }

        p.IsMale         = req.IsMale;
        p.Age            = req.Age;
        p.ActivityFactor = req.ActivityFactor;
        p.HeightCm       = req.HeightCm;
        p.UpdatedAt      = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return Ok(ToDto(p));
    }

    private static MacroDto ToDto(MacroProfile p) => new(
        p.Id.ToString(),
        p.IsMale,
        p.Age,
        p.ActivityFactor,
        p.HeightCm,
        p.UpdatedAt.ToString("o"));
}

public record MacroRequest(bool IsMale, int Age, double ActivityFactor, double? HeightCm);

public record MacroDto(
    string Id,
    bool IsMale,
    int Age,
    double ActivityFactor,
    double? HeightCm,
    string UpdatedAt);