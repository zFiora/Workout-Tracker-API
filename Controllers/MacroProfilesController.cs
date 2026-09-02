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

        if (req.Sex is not null)
        {
            var sex = req.Sex.Trim().ToLowerInvariant();
            if (sex is not ("male" or "female" or "unspecified"))
                return BadRequest(new { message = "sex must be 'male', 'female', or 'unspecified'." });
        }

        DateOnly? dateOfBirth = null;
        if (req.DateOfBirth is not null)
        {
            if (!DateOnly.TryParse(req.DateOfBirth, out var dob))
                return BadRequest(new { message = "dateOfBirth must be a valid date (YYYY-MM-DD)." });

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (dob > today)
                return BadRequest(new { message = "dateOfBirth cannot be in the future." });
            if (dob < today.AddYears(-120))
                return BadRequest(new { message = "dateOfBirth is not a valid date of birth." });

            dateOfBirth = dob;
        }

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

        // Absent/null sex or dateOfBirth means "leave unchanged" — never invent a value.
        if (req.Sex is not null) p.Sex = req.Sex.Trim().ToLowerInvariant();
        if (dateOfBirth is not null) p.DateOfBirth = dateOfBirth;

        await db.SaveChangesAsync();
        return Ok(ToDto(p));
    }

    private static MacroDto ToDto(MacroProfile p) => new(
        p.Id.ToString(),
        p.IsMale,
        p.Age,
        p.ActivityFactor,
        p.HeightCm,
        p.UpdatedAt.ToString("o"),
        p.Sex,
        p.DateOfBirth?.ToString("yyyy-MM-dd"));
}

public record MacroRequest(
    bool IsMale, int Age, double ActivityFactor, double? HeightCm,
    string? Sex = null, string? DateOfBirth = null);

public record MacroDto(
    string Id,
    bool IsMale,
    int Age,
    double ActivityFactor,
    double? HeightCm,
    string UpdatedAt,
    string? Sex,
    string? DateOfBirth);