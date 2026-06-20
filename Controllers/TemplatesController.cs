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

    // GET /api/templates — own templates + all public templates
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var templates = await db.Templates
            .Where(t => t.UserId == Me || t.IsPublic)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();
        return Ok(templates.Select(ToDto));
    }

    // GET /api/templates/friend/{userId} — a specific friend's public templates
    [HttpGet("friend/{userId:guid}")]
    public async Task<IActionResult> FriendTemplates(Guid userId)
    {
        var uid = Me;

        // Verify they are actually friends
        var areFriends = await db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == uid && f.AddresseeId == userId) ||
             (f.RequesterId == userId && f.AddresseeId == uid)));

        if (!areFriends)
            return Forbid();

        var templates = await db.Templates
            .Where(t => t.UserId == userId && t.IsPublic)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();

        return Ok(templates.Select(ToDto));
    }

    // POST /api/templates — create or update (upsert by Id)
    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertTemplateRequest req)
    {
        var existing = req.Id.HasValue
            ? await db.Templates.FindAsync(req.Id.Value)
            : null;

        if (existing is not null)
        {
            if (existing.UserId != Me) return Forbid();

            existing.Name        = req.Name;
            existing.IconPath    = req.IconPath;
            existing.ExerciseIds = req.ExerciseIds;
            existing.IsPublic    = req.IsPublic;
            existing.UpdatedAt   = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Ok(new { id = existing.Id });
        }
        else
        {
            var newId    = req.Id ?? Guid.NewGuid();
            var template = new Template
            {
                Id          = newId,
                UserId      = Me,
                Name        = req.Name,
                IconPath    = req.IconPath,
                ExerciseIds = req.ExerciseIds,
                IsPublic    = req.IsPublic,
                CreatedAt   = req.CreatedAt is not null
                                  ? DateTime.Parse(req.CreatedAt).ToUniversalTime()
                                  : DateTime.UtcNow,
                UpdatedAt   = req.UpdatedAt is not null
                                  ? DateTime.Parse(req.UpdatedAt).ToUniversalTime()
                                  : DateTime.UtcNow,
            };
            db.Templates.Add(template);
            await db.SaveChangesAsync();
            return Ok(new { id = newId });
        }
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

    private static TemplateDto ToDto(Template t) => new(
        t.Id.ToString(),
        t.UserId.ToString(),
        t.Name,
        t.IconPath,
        t.ExerciseIds,
        t.IsPublic,
        t.CreatedAt.ToString("o"),
        t.UpdatedAt.ToString("o"));
}

public record UpsertTemplateRequest(
    Guid? Id,
    string Name,
    string IconPath,
    List<int> ExerciseIds,
    bool IsPublic = false,
    string? CreatedAt = null,
    string? UpdatedAt = null);

public record TemplateDto(
    string Id,
    string UserId,
    string Name,
    string IconPath,
    List<int> ExerciseIds,
    bool IsPublic,
    string CreatedAt,
    string UpdatedAt);