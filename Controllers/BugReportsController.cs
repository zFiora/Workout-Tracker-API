using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

// Authenticated, self-service bug reporting: submit and view your own reports only.
// Triage (list-all/filter/status/notes) lives in AdminController — never here.
[ApiController]
[Route("api/bug-reports")]
[Authorize]
public class BugReportsController(AppDbContext db) : ControllerBase
{
    // Screenshots run larger than avatars (full-screen captures vs. a small square),
    // so this cap is looser than UsersController's 2MB avatar limit, but still bounded.
    private const long MaxScreenshotBytes = 5 * 1024 * 1024;
    private static readonly string[] AllowedScreenshotTypes = ["image/jpeg", "image/png", "image/webp"];

    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/bug-reports — multipart form: text fields + an optional screenshot.
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromForm] string title,
        [FromForm] string description,
        [FromForm] string? appVersion,
        [FromForm] string? platform,
        [FromForm] string? deviceInfo,
        [FromForm] string? appScreen,
        [FromForm] string? errorDetails,
        IFormFile? screenshot)
    {
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { message = "A title is required." });
        if (string.IsNullOrWhiteSpace(description))
            return BadRequest(new { message = "A description is required." });

        string? screenshotBase64 = null;
        string? screenshotContentType = null;

        if (screenshot is { Length: > 0 })
        {
            if (!AllowedScreenshotTypes.Contains(screenshot.ContentType.ToLower()))
                return BadRequest(new { message = "Only JPEG, PNG and WebP screenshots are allowed." });

            if (screenshot.Length > MaxScreenshotBytes)
                return BadRequest(new { message = "Screenshot must be under 5MB." });

            using var stream = new MemoryStream();
            await screenshot.CopyToAsync(stream);
            screenshotBase64 = Convert.ToBase64String(stream.ToArray());
            screenshotContentType = screenshot.ContentType.ToLower();
        }

        var report = new BugReport
        {
            UserId = Me,
            Title = title.Trim(),
            Description = description.Trim(),
            AppVersion = appVersion?.Trim(),
            Platform = platform?.Trim(),
            DeviceInfo = deviceInfo?.Trim(),
            AppScreen = appScreen?.Trim(),
            ErrorDetails = errorDetails?.Trim(),
            ScreenshotBase64 = screenshotBase64,
            ScreenshotContentType = screenshotContentType,
        };

        db.BugReports.Add(report);
        await db.SaveChangesAsync();

        return Ok(ToDto(report));
    }

    // GET /api/bug-reports/mine — own history, newest first. No screenshot bytes here
    // (HasScreenshot only) — a per-user list stays small, but there's no reason to ship
    // image payloads for rows the caller isn't currently looking at.
    [HttpGet("mine")]
    public async Task<IActionResult> GetMine()
    {
        var uid = Me;
        var reports = await db.BugReports.AsNoTracking()
            .Where(r => r.UserId == uid)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return Ok(reports.Select(ToListItemDto));
    }

    // GET /api/bug-reports/mine/{id} — full own detail, including screenshot.
    // AdminNotes is never included here — internal only. 404 (not 403) whether the id
    // doesn't exist or belongs to someone else, so a non-owner learns nothing either way.
    [HttpGet("mine/{id:guid}")]
    public async Task<IActionResult> GetMineDetail(Guid id)
    {
        var uid = Me;
        var report = await db.BugReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == uid);
        if (report is null) return NotFound();

        return Ok(ToDto(report));
    }

    private static BugReportListItemDto ToListItemDto(BugReport r) => new(
        r.Id.ToString(), r.Title, r.Status.ToString(), r.Priority.ToString(),
        r.Platform, r.AppVersion, r.ScreenshotBase64 is not null, r.CreatedAt);

    private static BugReportDto ToDto(BugReport r) => new(
        r.Id.ToString(), r.Title, r.Description, r.Status.ToString(), r.Priority.ToString(),
        r.AppVersion, r.Platform, r.DeviceInfo, r.AppScreen, r.ErrorDetails,
        r.ScreenshotBase64, r.ScreenshotContentType, r.CreatedAt, r.UpdatedAt);
}

public record BugReportListItemDto(
    string Id, string Title, string Status, string Priority,
    string? Platform, string? AppVersion, bool HasScreenshot, DateTime CreatedAt);

public record BugReportDto(
    string Id, string Title, string Description, string Status, string Priority,
    string? AppVersion, string? Platform, string? DeviceInfo, string? AppScreen, string? ErrorDetails,
    string? ScreenshotBase64, string? ScreenshotContentType,
    DateTime CreatedAt, DateTime UpdatedAt);
