using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;
using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController(AppDbContext db) : ControllerBase
{
    // "Active" here means workout recency (a coarse health signal), not the IsActive
    // account-status flag toggled by SetStatus below — those are deliberately different axes.
    private const int ActiveWindowDays = 30;
    private const int RecentWorkoutsLimit = 5;

    // GET /api/admin/dashboard
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var weekStart = now.Date.AddDays(-7);
        var monthStart = now.Date.AddDays(-30);
        var activeCutoff = now.Date.AddDays(-ActiveWindowDays);

        var totalUsers = await db.Users.CountAsync();
        var newToday = await db.Users.CountAsync(u => u.CreatedAt >= todayStart);
        var newWeek = await db.Users.CountAsync(u => u.CreatedAt >= weekStart);
        var newMonth = await db.Users.CountAsync(u => u.CreatedAt >= monthStart);
        var active = await db.Users.CountAsync(u => u.LastWorkoutDate != null && u.LastWorkoutDate >= activeCutoff);

        var completed = db.WorkoutSessions.Where(s => s.DeletedAt == null);
        var totalCompleted = await completed.CountAsync();
        var todayCompleted = await completed.CountAsync(s => s.EndedAt >= todayStart);
        var weekCompleted = await completed.CountAsync(s => s.EndedAt >= weekStart);
        var monthCompleted = await completed.CountAsync(s => s.EndedAt >= monthStart);

        return Ok(new AdminDashboardDto(
            new UserMetricsDto(totalUsers, newToday, newWeek, newMonth, active),
            new WorkoutMetricsDto(totalCompleted, todayCompleted, weekCompleted, monthCompleted)));
    }

    // GET /api/admin/users?search=&sortBy=&sortDir=&status=&page=&pageSize=
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? search,
        [FromQuery] string sortBy = "createdAt",
        [FromQuery] string sortDir = "desc",
        [FromQuery] string status = "all",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u =>
                u.Email.ToLower().Contains(s) ||
                u.Username.ToLower().Contains(s) ||
                (u.DisplayName != null && u.DisplayName.ToLower().Contains(s)));
        }

        query = status.ToLower() switch
        {
            "active" => query.Where(u => u.IsActive),
            "suspended" => query.Where(u => !u.IsActive),
            _ => query,
        };

        var desc = sortDir.Equals("desc", StringComparison.OrdinalIgnoreCase);
        query = sortBy.ToLower() switch
        {
            "username" => desc ? query.OrderByDescending(u => u.Username) : query.OrderBy(u => u.Username),
            "email" => desc ? query.OrderByDescending(u => u.Email) : query.OrderBy(u => u.Email),
            "currentstreak" => desc ? query.OrderByDescending(u => u.CurrentStreak) : query.OrderBy(u => u.CurrentStreak),
            "beststreak" => desc ? query.OrderByDescending(u => u.BestStreak) : query.OrderBy(u => u.BestStreak),
            "lastworkoutdate" => desc ? query.OrderByDescending(u => u.LastWorkoutDate) : query.OrderBy(u => u.LastWorkoutDate),
            _ => desc ? query.OrderByDescending(u => u.CreatedAt) : query.OrderBy(u => u.CreatedAt),
        };

        var totalCount = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new AdminUserListResponse(items.Select(ToListItemDto).ToList(), totalCount, page, pageSize));
    }

    // GET /api/admin/users/{id}
    [HttpGet("users/{id:guid}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var user = await db.Users.AsNoTracking()
            .Include(u => u.MacroProfile)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        var workoutCount = await db.WorkoutSessions.CountAsync(s => s.UserId == id && s.DeletedAt == null);
        var templateCount = await db.Templates.CountAsync(t => t.UserId == id && t.DeletedAt == null);
        var measurementCount = await db.Measurements.CountAsync(m => m.UserId == id);
        var prEventCount = await db.PrEvents.CountAsync(p => p.UserId == id);

        var recentWorkouts = await db.WorkoutSessions.AsNoTracking()
            .Where(s => s.UserId == id && s.DeletedAt == null)
            .OrderByDescending(s => s.EndedAt)
            .Take(RecentWorkoutsLimit)
            .Select(s => new AdminRecentWorkoutDto(s.Id.ToString(), s.TemplateName, s.StartedAt, s.EndedAt, s.DurationMs))
            .ToListAsync();

        var weeksSinceJoined = Math.Max(1.0, (DateTime.UtcNow - user.CreatedAt).TotalDays / 7.0);
        var workoutsPerWeek = Math.Round(workoutCount / weeksSinceJoined, 1);

        return Ok(ToDetailDto(user, workoutCount, templateCount, measurementCount, prEventCount, workoutsPerWeek, recentWorkouts));
    }

    // PATCH /api/admin/users/{id}/status — the one reversible account action in this
    // phase (suspend/reactivate). No hard delete: User has no soft-delete infrastructure
    // today, so a destructive delete endpoint is deliberately out of scope for now.
    [HttpPatch("users/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetUserStatusRequest req)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        user.IsActive = req.IsActive;
        await db.SaveChangesAsync();

        return Ok(ToListItemDto(user));
    }

    private static AdminUserListItemDto ToListItemDto(User u) => new(
        u.Id.ToString(), u.Email, u.Username, u.DisplayName,
        u.Role.ToString(), u.IsActive,
        StreakCalculator.EffectiveCurrentStreak(u.CurrentStreak, u.LastQualifyingWorkoutAt, u.TimeZoneId, DateTime.UtcNow),
        u.BestStreak, u.LastWorkoutDate?.ToString("yyyy-MM-dd"), u.CreatedAt);

    private static AdminUserDetailDto ToDetailDto(
        User u, int workoutCount, int templateCount, int measurementCount, int prEventCount,
        double workoutsPerWeek, IReadOnlyList<AdminRecentWorkoutDto> recentWorkouts) => new(
        u.Id.ToString(), u.Email, u.Username, u.DisplayName,
        u.Role.ToString(), u.IsActive, u.CreatedAt, u.TimeZoneId,
        StreakCalculator.EffectiveCurrentStreak(u.CurrentStreak, u.LastQualifyingWorkoutAt, u.TimeZoneId, DateTime.UtcNow),
        u.BestStreak, u.LastWorkoutDate?.ToString("yyyy-MM-dd"), u.LastQualifyingWorkoutAt,
        u.MacroProfile?.Sex, u.MacroProfile?.DateOfBirth, u.MacroProfile?.HeightCm,
        workoutCount, templateCount, measurementCount, prEventCount, workoutsPerWeek, recentWorkouts);
}

public record UserMetricsDto(int Total, int NewToday, int NewThisWeek, int NewThisMonth, int Active);
public record WorkoutMetricsDto(int TotalCompleted, int Today, int ThisWeek, int ThisMonth);
public record AdminDashboardDto(UserMetricsDto Users, WorkoutMetricsDto Workouts);

public record AdminUserListItemDto(
    string Id, string Email, string Username, string? DisplayName,
    string Role, bool IsActive, int CurrentStreak, int BestStreak,
    string? LastWorkoutDate, DateTime CreatedAt);

public record AdminUserListResponse(IReadOnlyList<AdminUserListItemDto> Items, int TotalCount, int Page, int PageSize);

public record AdminRecentWorkoutDto(string Id, string? TemplateName, DateTime StartedAt, DateTime EndedAt, long DurationMs);

public record AdminUserDetailDto(
    string Id, string Email, string Username, string? DisplayName,
    string Role, bool IsActive, DateTime CreatedAt, string? TimeZoneId,
    int CurrentStreak, int BestStreak, string? LastWorkoutDate, DateTime? LastQualifyingWorkoutAt,
    string? Sex, DateOnly? DateOfBirth, double? HeightCm,
    int WorkoutCount, int TemplateCount, int MeasurementCount, int PrEventCount,
    double WorkoutsPerWeek, IReadOnlyList<AdminRecentWorkoutDto> RecentWorkouts);

public record SetUserStatusRequest(bool IsActive);
