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

    // GET /api/templates — own templates + all public templates.
    // includeDeleted=true additionally surfaces the caller's own soft-deleted templates
    // (never other users' deleted public ones) so a second device can reconcile deletes.
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] bool includeDeleted = false,
        [FromQuery] int? sinceDays = null)
    {
        var uid = Me;
        IQueryable<Template> q = db.Templates.Where(t => t.UserId == uid || t.IsPublic);

        if (!includeDeleted)
        {
            q = q.Where(t => t.DeletedAt == null);
        }
        else
        {
            var cutoff = sinceDays.HasValue ? DateTime.UtcNow.AddDays(-sinceDays.Value) : (DateTime?)null;
            q = q.Where(t =>
                t.DeletedAt == null ||
                (t.UserId == uid && (cutoff == null || t.DeletedAt >= cutoff)));
        }

        var templates = await q.OrderByDescending(t => t.UpdatedAt).ToListAsync();
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
            .Where(t => t.UserId == userId && t.IsPublic && t.DeletedAt == null)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();

        return Ok(templates.Select(ToDto));
    }

    // POST /api/templates — create or update (upsert by Id), last-writer-wins by UpdatedAt.
    // A stale edit (older than what's stored, or not newer than a recorded delete) is a
    // no-op that just returns the current server copy — the winning row either way.
    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertTemplateRequest req)
    {
        var existing = req.Id.HasValue
            ? await db.Templates.FindAsync(req.Id.Value)
            : null;

        var incomingUpdatedAt = req.UpdatedAt is not null
            ? DateTime.Parse(req.UpdatedAt).ToUniversalTime()
            : DateTime.UtcNow;

        if (existing is not null)
        {
            if (existing.UserId != Me) return Forbid();

            if (existing.DeletedAt is not null && incomingUpdatedAt <= existing.DeletedAt)
                return Ok(ToDto(existing));

            if (incomingUpdatedAt < existing.UpdatedAt)
                return Ok(ToDto(existing));

            existing.Name        = req.Name;
            existing.IconPath    = req.IconPath;
            existing.ExerciseIds = req.ExerciseIds;
            existing.IsPublic    = req.IsPublic;
            existing.UpdatedAt   = incomingUpdatedAt;
            existing.DeletedAt   = null; // an edit newer than the delete undeletes it

            await db.SaveChangesAsync();
            return Ok(ToDto(existing));
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
                UpdatedAt   = incomingUpdatedAt,
            };
            db.Templates.Add(template);
            await db.SaveChangesAsync();
            return Ok(ToDto(template));
        }
    }

    // DELETE /api/templates/{id} — idempotent soft-delete: 204 whether it existed,
    // was already deleted, or isn't owned by the caller.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var t = await db.Templates.FirstOrDefaultAsync(t => t.Id == id && t.UserId == Me);
        if (t is not null && t.DeletedAt is null)
        {
            t.DeletedAt = DateTime.UtcNow;
            t.UpdatedAt = t.DeletedAt.Value;
            await db.SaveChangesAsync();
        }
        return NoContent();
    }

    // POST /api/templates/{templateId}/share
    [HttpPost("{templateId:guid}/share")]
    public async Task<IActionResult> Share(Guid templateId, [FromBody] ShareTemplateRequest req)
    {
        var uid = Me;

        if (!Guid.TryParse(req.FriendUserId, out var friendId))
            return BadRequest(new { message = "Invalid friendUserId." });

        var template = await db.Templates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.DeletedAt == null);
        if (template is null) return NotFound(new { message = "Template not found." });
        if (template.UserId != uid) return Forbid();

        var areFriends = await db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == uid && f.AddresseeId == friendId) ||
             (f.RequesterId == friendId && f.AddresseeId == uid)));
        if (!areFriends)
            return BadRequest(new { message = "Target user is not in your friends list." });

        var alreadyShared = await db.SharedTemplates.AnyAsync(s =>
            s.TemplateId == templateId && s.SharedWithUserId == friendId && !s.IsDeleted);
        if (alreadyShared)
            return Conflict(new { message = "Template already shared with this friend." });

        var share = new SharedTemplate
        {
            TemplateId = templateId,
            OwnerUserId = uid,
            SharedWithUserId = friendId,
        };
        db.SharedTemplates.Add(share);
        await db.SaveChangesAsync();

        return Created($"/api/templates/{templateId}/share", new SharedTemplateDto(
            share.Id.ToString(),
            template.Id.ToString(),
            template.Name,
            share.OwnerUserId.ToString(),
            share.SharedWithUserId.ToString(),
            share.CreatedAt.ToString("o")));
    }

    // GET /api/templates/saved
    [HttpGet("saved")]
    public async Task<IActionResult> ListSaved()
    {
        var saved = await db.SavedTemplates
            .Where(s => s.UserId == Me && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        return Ok(saved.Select(ToSavedDto));
    }

    // DELETE /api/templates/saved/{id}
    [HttpDelete("saved/{id:guid}")]
    public async Task<IActionResult> DeleteSaved(Guid id)
    {
        var saved = await db.SavedTemplates
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == Me && !s.IsDeleted);
        if (saved is null) return NotFound();

        saved.IsDeleted = true;
        saved.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }

    internal static SavedTemplateDto ToSavedDto(SavedTemplate s) => new(
        s.Id.ToString(),
        s.SharedTemplateId.ToString(),
        s.SourceTemplateId.ToString(),
        s.Name,
        s.IconPath,
        s.ExerciseIds,
        s.CreatedAt.ToString("o"));

    internal static TemplateDto ToDto(Template t) => new(
        t.Id.ToString(),
        t.UserId.ToString(),
        t.Name,
        t.IconPath,
        t.ExerciseIds,
        t.IsPublic,
        t.CreatedAt.ToString("o"),
        t.UpdatedAt.ToString("o"),
        t.DeletedAt?.ToString("o"));
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
    string UpdatedAt,
    string? DeletedAt);

public record ShareTemplateRequest(string FriendUserId);

public record SharedTemplateDto(
    string Id,
    string TemplateId,
    string TemplateName,
    string OwnerUserId,
    string SharedWithUserId,
    string CreatedAt);

public record SavedTemplateDto(
    string Id,
    string SharedTemplateId,
    string SourceTemplateId,
    string Name,
    string IconPath,
    List<int> ExerciseIds,
    string CreatedAt);