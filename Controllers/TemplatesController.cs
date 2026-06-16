using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/templates")]
[Authorize]
public class TemplatesController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/templates
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var templates = await db.Templates
            .Where(t => t.UserId == Me || t.IsPublic)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();
        return Ok(templates.Select(TemplateDto.From));
    }

    // POST /api/templates
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertTemplateRequest req)
    {
        var t = new Template
        {
            UserId = Me,
            Name = req.Name,
            IconPath = req.IconPath,
            ExerciseIds = req.ExerciseIds,
            IsPublic = req.IsPublic,
            CreatedAt = req.CreatedAt?.ToUniversalTime() ?? DateTime.UtcNow,
            UpdatedAt = req.UpdatedAt?.ToUniversalTime() ?? DateTime.UtcNow,
        };
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        return Created($"/api/templates/{t.Id}", TemplateDto.From(t));
    }

    // PATCH /api/templates/{id}
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertTemplateRequest req)
    {
        var t = await db.Templates
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == Me);
        if (t is null) return NotFound();

        t.Name = req.Name;
        t.IconPath = req.IconPath;
        t.ExerciseIds = req.ExerciseIds;
        t.IsPublic = req.IsPublic;
        t.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return Ok(TemplateDto.From(t));
    }

    // DELETE /api/templates/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var t = await db.Templates
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == Me);
        if (t is null) return NotFound();
        db.Templates.Remove(t);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record UpsertTemplateRequest(
    string Name, string IconPath, List<int> ExerciseIds,
    bool IsPublic = false, DateTime? CreatedAt = null, DateTime? UpdatedAt = null);

public record TemplateDto(
    Guid Id, string Name, string IconPath,
    List<int> ExerciseIds, bool IsPublic, DateTime CreatedAt, DateTime UpdatedAt)
{
    public static TemplateDto From(Template t) => new(
        t.Id, t.Name, t.IconPath, t.ExerciseIds, t.IsPublic, t.CreatedAt, t.UpdatedAt);
}