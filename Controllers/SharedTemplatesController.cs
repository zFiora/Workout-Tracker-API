using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/shared-templates")]
[Authorize]
public class SharedTemplatesController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/shared-templates/{sharedTemplateId}/save
    [HttpPost("{sharedTemplateId:guid}/save")]
    public async Task<IActionResult> Save(Guid sharedTemplateId)
    {
        var uid = Me;

        var share = await db.SharedTemplates
            .Include(s => s.Template)
            .FirstOrDefaultAsync(s => s.Id == sharedTemplateId && !s.IsDeleted);
        if (share is null) return NotFound(new { message = "Shared template not found." });
        if (share.SharedWithUserId != uid) return Forbid();
        if (share.Template.DeletedAt is not null) return NotFound(new { message = "Source template no longer exists." });

        var alreadySaved = await db.SavedTemplates.AnyAsync(s =>
            s.SharedTemplateId == sharedTemplateId && s.UserId == uid && !s.IsDeleted);
        if (alreadySaved) return Conflict(new { message = "Template already saved." });

        var saved = new SavedTemplate
        {
            UserId = uid,
            SharedTemplateId = share.Id,
            SourceTemplateId = share.TemplateId,
            Name = share.Template.Name,
            IconPath = share.Template.IconPath,
            ExerciseIds = [.. share.Template.ExerciseIds],
        };
        db.SavedTemplates.Add(saved);
        await db.SaveChangesAsync();

        return Created($"/api/templates/saved/{saved.Id}", TemplatesController.ToSavedDto(saved));
    }
}
