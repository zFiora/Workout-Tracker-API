using System.Text.Json;
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
public class AdminController(AppDbContext db, AuditLogService auditLog) : ControllerBase
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
        auditLog.RecordForCurrentAdmin(
            User,
            req.IsActive ? "user.reactivate" : "user.suspend",
            "User",
            id.ToString(),
            $"{(req.IsActive ? "Reactivated" : "Suspended")} user {user.Username}",
            new { isActive = req.IsActive });
        await db.SaveChangesAsync();

        return Ok(ToListItemDto(user));
    }

    // GET /api/admin/workouts?userId=&userSearch=&templateName=&startDate=&endDate=&sortDir=&page=&pageSize=
    // Read-only browsing across every user's workout history — never mutates anything.
    // Soft-deleted sessions are excluded, consistent with GetDashboard/GetUser.
    [HttpGet("workouts")]
    public async Task<IActionResult> GetWorkouts(
        [FromQuery] Guid? userId,
        [FromQuery] string? userSearch,
        [FromQuery] string? templateName,
        [FromQuery] DateOnly? startDate,
        [FromQuery] DateOnly? endDate,
        [FromQuery] string sortDir = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.WorkoutSessions.AsNoTracking()
            .Include(s => s.User)
            .Where(s => s.DeletedAt == null)
            .AsQueryable();

        if (userId.HasValue)
            query = query.Where(s => s.UserId == userId.Value);

        if (!string.IsNullOrWhiteSpace(userSearch))
        {
            var s = userSearch.Trim().ToLower();
            query = query.Where(w =>
                w.User.Username.ToLower().Contains(s) ||
                w.User.Email.ToLower().Contains(s) ||
                (w.User.DisplayName != null && w.User.DisplayName.ToLower().Contains(s)));
        }

        if (!string.IsNullOrWhiteSpace(templateName))
        {
            var t = templateName.Trim().ToLower();
            query = query.Where(w => w.TemplateName != null && w.TemplateName.ToLower().Contains(t));
        }

        if (startDate.HasValue)
        {
            var start = DateTime.SpecifyKind(startDate.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            query = query.Where(w => w.EndedAt >= start);
        }

        if (endDate.HasValue)
        {
            var end = DateTime.SpecifyKind(endDate.Value.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc);
            query = query.Where(w => w.EndedAt <= end);
        }

        var ascending = sortDir.Equals("asc", StringComparison.OrdinalIgnoreCase);
        query = ascending ? query.OrderBy(w => w.EndedAt) : query.OrderByDescending(w => w.EndedAt);

        var totalCount = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new AdminWorkoutListResponse(items.Select(ToWorkoutListItemDto).ToList(), totalCount, page, pageSize));
    }

    // GET /api/admin/workouts/{id} — full detail, including the raw exercise/set
    // breakdown (Logs), for one session. Read-only: no PATCH/DELETE is exposed here.
    [HttpGet("workouts/{id:guid}")]
    public async Task<IActionResult> GetWorkout(Guid id)
    {
        var session = await db.WorkoutSessions.AsNoTracking()
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (session is null) return NotFound();

        var (exerciseCount, setCount, totalVolume) = ComputeSessionStats(session.LogsJson);

        return Ok(new AdminWorkoutDetailDto(
            session.Id.ToString(), session.UserId.ToString(), session.User.Username, session.User.DisplayName, session.User.Email,
            session.TemplateName, session.StartedAt, session.EndedAt, session.DurationMs,
            exerciseCount, setCount, totalVolume,
            session.LogsJson.RootElement.Clone(), session.DeletedAt?.ToString("o")));
    }

    // Each top-level LogsJson array entry is one exercise; "sets" holds the
    // actually-completed sets (as opposed to "plannedSets", the pre-workout
    // checklist) — same volume definition as WorkoutSessionsController's
    // friends-ranking (sum of weight*reps across every completed set).
    private static (int ExerciseCount, int SetCount, double TotalVolume) ComputeSessionStats(JsonDocument logsJson)
    {
        var exerciseCount = 0;
        var setCount = 0;

        foreach (var log in logsJson.RootElement.EnumerateArray())
        {
            exerciseCount++;
            if (log.TryGetProperty("sets", out var sets))
                setCount += sets.GetArrayLength();
        }

        var totalVolume = WorkoutSessionsController.ComputeTotalVolume(logsJson);
        return (exerciseCount, setCount, totalVolume);
    }

    private static AdminWorkoutListItemDto ToWorkoutListItemDto(WorkoutSession s)
    {
        var (exerciseCount, setCount, totalVolume) = ComputeSessionStats(s.LogsJson);
        return new AdminWorkoutListItemDto(
            s.Id.ToString(), s.UserId.ToString(), s.User.Username, s.User.DisplayName,
            s.TemplateName, s.StartedAt, s.EndedAt, s.DurationMs,
            exerciseCount, setCount, totalVolume);
    }

    // GET /api/admin/bug-reports?status=&userSearch=&startDate=&endDate=&sortDir=&page=&pageSize=
    // Lightweight list — no screenshot bytes, matching the workouts-list precedent.
    [HttpGet("bug-reports")]
    public async Task<IActionResult> GetBugReports(
        [FromQuery] string? status,
        [FromQuery] string? userSearch,
        [FromQuery] DateOnly? startDate,
        [FromQuery] DateOnly? endDate,
        [FromQuery] string sortDir = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.BugReports.AsNoTracking().Include(r => r.User).AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<BugReportStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(r => r.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(userSearch))
        {
            var s = userSearch.Trim().ToLower();
            query = query.Where(r =>
                r.User.Username.ToLower().Contains(s) ||
                r.User.Email.ToLower().Contains(s) ||
                (r.User.DisplayName != null && r.User.DisplayName.ToLower().Contains(s)));
        }

        if (startDate.HasValue)
        {
            var start = DateTime.SpecifyKind(startDate.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            query = query.Where(r => r.CreatedAt >= start);
        }

        if (endDate.HasValue)
        {
            var end = DateTime.SpecifyKind(endDate.Value.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc);
            query = query.Where(r => r.CreatedAt <= end);
        }

        var ascending = sortDir.Equals("asc", StringComparison.OrdinalIgnoreCase);
        query = ascending ? query.OrderBy(r => r.CreatedAt) : query.OrderByDescending(r => r.CreatedAt);

        var totalCount = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new AdminBugReportListResponse(items.Select(ToBugReportListItemDto).ToList(), totalCount, page, pageSize));
    }

    // GET /api/admin/bug-reports/{id} — full detail, including screenshot and AdminNotes.
    [HttpGet("bug-reports/{id:guid}")]
    public async Task<IActionResult> GetBugReport(Guid id)
    {
        var report = await db.BugReports.AsNoTracking()
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (report is null) return NotFound();

        return Ok(ToBugReportDetailDto(report));
    }

    // PATCH /api/admin/bug-reports/{id} — updates whichever of Status/AdminNotes is
    // provided. Fully reversible triage action; no confirmation dialog is warranted
    // client-side (unlike suspending a user's account).
    [HttpPatch("bug-reports/{id:guid}")]
    public async Task<IActionResult> UpdateBugReport(Guid id, [FromBody] UpdateBugReportRequest req)
    {
        var report = await db.BugReports.Include(r => r.User).FirstOrDefaultAsync(r => r.Id == id);
        if (report is null) return NotFound();

        if (req.Status is not null)
        {
            if (!Enum.TryParse<BugReportStatus>(req.Status, true, out var parsedStatus))
                return BadRequest(new { message = "Invalid status." });

            if (parsedStatus != report.Status)
            {
                auditLog.RecordForCurrentAdmin(
                    User,
                    "bugreport.status_change",
                    "BugReport",
                    id.ToString(),
                    $"Changed bug report '{report.Title}' status from {report.Status} to {parsedStatus}",
                    new { from = report.Status.ToString(), to = parsedStatus.ToString() });
            }
            report.Status = parsedStatus;
        }

        if (req.AdminNotes is not null)
            report.AdminNotes = req.AdminNotes;

        report.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ToBugReportDetailDto(report));
    }

    private static AdminBugReportListItemDto ToBugReportListItemDto(BugReport r) => new(
        r.Id.ToString(), r.UserId.ToString(), r.User.Username, r.User.DisplayName,
        r.Title, r.Status.ToString(), r.Priority.ToString(), r.Platform, r.AppVersion,
        r.ScreenshotBase64 is not null, r.CreatedAt);

    private static AdminBugReportDetailDto ToBugReportDetailDto(BugReport r) => new(
        r.Id.ToString(), r.UserId.ToString(), r.User.Username, r.User.DisplayName, r.User.Email,
        r.Title, r.Description, r.Status.ToString(), r.Priority.ToString(),
        r.AppVersion, r.Platform, r.DeviceInfo, r.AppScreen, r.ErrorDetails,
        r.ScreenshotBase64, r.ScreenshotContentType, r.AdminNotes, r.CreatedAt, r.UpdatedAt);

    // GET /api/admin/analytics/overview?startDate=&endDate=
    // ActiveUsersInRange is deliberately distinct from GetDashboard's fixed 30-day "Active" —
    // this page is date-range-scoped by design, not tied to that fixed window.
    [HttpGet("analytics/overview")]
    public async Task<IActionResult> GetAnalyticsOverview([FromQuery] DateOnly? startDate, [FromQuery] DateOnly? endDate)
    {
        var (start, end, _, _) = ResolveRange(startDate, endDate);

        var totalUsers = await db.Users.CountAsync();
        var newUsersInRange = await db.Users.CountAsync(u => u.CreatedAt >= start && u.CreatedAt <= end);

        var sessionsInRange = db.WorkoutSessions.Where(s => s.DeletedAt == null && s.EndedAt >= start && s.EndedAt <= end);
        var completedWorkoutsInRange = await sessionsInRange.CountAsync();
        var activeUsersInRange = await sessionsInRange.Select(s => s.UserId).Distinct().CountAsync();

        var avgWorkoutsPerActiveUser = activeUsersInRange == 0
            ? 0
            : Math.Round((double)completedWorkoutsInRange / activeUsersInRange, 1);

        return Ok(new AnalyticsOverviewDto(
            totalUsers, newUsersInRange, activeUsersInRange, completedWorkoutsInRange, avgWorkoutsPerActiveUser));
    }

    // GET /api/admin/analytics/user-growth?startDate=&endDate= — new registrations/day.
    [HttpGet("analytics/user-growth")]
    public async Task<IActionResult> GetUserGrowth([FromQuery] DateOnly? startDate, [FromQuery] DateOnly? endDate)
    {
        var (start, end, startDay, endDay) = ResolveRange(startDate, endDate);

        var grouped = await db.Users
            .Where(u => u.CreatedAt >= start && u.CreatedAt <= end)
            .GroupBy(u => u.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync();

        var byDay = grouped.ToDictionary(g => DateOnly.FromDateTime(g.Date), g => g.Count);
        return Ok(new AnalyticsSeriesResponse(FillDailySeries(byDay, startDay, endDay)));
    }

    // GET /api/admin/analytics/active-users?startDate=&endDate= — daily active users (DAU):
    // distinct users per day, NOT total session count — see workout-activity for that.
    [HttpGet("analytics/active-users")]
    public async Task<IActionResult> GetActiveUsersSeries([FromQuery] DateOnly? startDate, [FromQuery] DateOnly? endDate)
    {
        var (start, end, startDay, endDay) = ResolveRange(startDate, endDate);

        var grouped = await db.WorkoutSessions
            .Where(s => s.DeletedAt == null && s.EndedAt >= start && s.EndedAt <= end)
            .GroupBy(s => s.EndedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Select(s => s.UserId).Distinct().Count() })
            .ToListAsync();

        var byDay = grouped.ToDictionary(g => DateOnly.FromDateTime(g.Date), g => g.Count);
        return Ok(new AnalyticsSeriesResponse(FillDailySeries(byDay, startDay, endDay)));
    }

    // GET /api/admin/analytics/workout-activity?startDate=&endDate= — completed sessions/day
    // (can exceed active-users on a given day if users log more than one workout that day).
    [HttpGet("analytics/workout-activity")]
    public async Task<IActionResult> GetWorkoutActivitySeries([FromQuery] DateOnly? startDate, [FromQuery] DateOnly? endDate)
    {
        var (start, end, startDay, endDay) = ResolveRange(startDate, endDate);

        var grouped = await db.WorkoutSessions
            .Where(s => s.DeletedAt == null && s.EndedAt >= start && s.EndedAt <= end)
            .GroupBy(s => s.EndedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync();

        var byDay = grouped.ToDictionary(g => DateOnly.FromDateTime(g.Date), g => g.Count);
        return Ok(new AnalyticsSeriesResponse(FillDailySeries(byDay, startDay, endDay)));
    }

    // GET /api/admin/analytics/template-popularity?startDate=&endDate=&limit=10 — ranks by how
    // often a template name was actually used to complete a workout (not shares/saves).
    [HttpGet("analytics/template-popularity")]
    public async Task<IActionResult> GetTemplatePopularity(
        [FromQuery] DateOnly? startDate, [FromQuery] DateOnly? endDate, [FromQuery] int limit = 10)
    {
        var (start, end, _, _) = ResolveRange(startDate, endDate);
        limit = Math.Clamp(limit, 1, 50);

        var grouped = await db.WorkoutSessions
            .Where(s => s.DeletedAt == null && s.EndedAt >= start && s.EndedAt <= end)
            .GroupBy(s => s.TemplateName)
            .Select(g => new { TemplateName = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(limit)
            .ToListAsync();

        var items = grouped
            .Select(g => new TemplatePopularityDto(g.TemplateName ?? "Ad-hoc workout", g.Count))
            .ToList();

        return Ok(new AnalyticsTemplatePopularityResponse(items));
    }

    private const int DefaultAnalyticsRangeDays = 30;
    private const int MaxAnalyticsRangeDays = 366;

    private static (DateTime StartUtc, DateTime EndUtc, DateOnly StartDate, DateOnly EndDate) ResolveRange(
        DateOnly? startDate, DateOnly? endDate)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var end = endDate ?? today;
        var start = startDate ?? end.AddDays(-(DefaultAnalyticsRangeDays - 1));

        if (start > end)
            (start, end) = (end, start);

        if (end.DayNumber - start.DayNumber > MaxAnalyticsRangeDays)
            start = end.AddDays(-MaxAnalyticsRangeDays);

        var startUtc = DateTime.SpecifyKind(start.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(end.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc);
        return (startUtc, endUtc, start, end);
    }

    private static List<DailyCountDto> FillDailySeries(Dictionary<DateOnly, int> byDay, DateOnly start, DateOnly end)
    {
        var series = new List<DailyCountDto>();
        for (var day = start; day <= end; day = day.AddDays(1))
            series.Add(new DailyCountDto(day, byDay.GetValueOrDefault(day)));
        return series;
    }

    // GET /api/admin/audit-logs?action=&adminSearch=&targetType=&targetId=&startDate=&endDate=&sortDir=&page=&pageSize=
    // Read-only, append-only from the API's perspective — no PATCH/DELETE is exposed
    // for audit rows anywhere.
    [HttpGet("audit-logs")]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] string? action,
        [FromQuery] string? adminSearch,
        [FromQuery] string? targetType,
        [FromQuery] string? targetId,
        [FromQuery] DateOnly? startDate,
        [FromQuery] DateOnly? endDate,
        [FromQuery] string sortDir = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.AdminAuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);

        if (!string.IsNullOrWhiteSpace(adminSearch))
        {
            var s = adminSearch.Trim().ToLower();
            query = query.Where(a => a.AdminUsername.ToLower().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(targetType))
            query = query.Where(a => a.TargetType == targetType);

        if (!string.IsNullOrWhiteSpace(targetId))
            query = query.Where(a => a.TargetId == targetId);

        if (startDate.HasValue)
        {
            var start = DateTime.SpecifyKind(startDate.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            query = query.Where(a => a.CreatedAt >= start);
        }

        if (endDate.HasValue)
        {
            var end = DateTime.SpecifyKind(endDate.Value.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc);
            query = query.Where(a => a.CreatedAt <= end);
        }

        var ascending = sortDir.Equals("asc", StringComparison.OrdinalIgnoreCase);
        query = ascending ? query.OrderBy(a => a.CreatedAt) : query.OrderByDescending(a => a.CreatedAt);

        var totalCount = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new AdminAuditLogListResponse(items.Select(ToAuditLogItemDto).ToList(), totalCount, page, pageSize));
    }

    private static AdminAuditLogItemDto ToAuditLogItemDto(AdminAuditLog a) => new(
        a.Id.ToString(), a.AdminUserId.ToString(), a.AdminUsername, a.Action,
        a.TargetType, a.TargetId, a.Description, a.Metadata, a.CreatedAt);

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

public record AdminWorkoutListItemDto(
    string Id, string UserId, string Username, string? UserDisplayName,
    string? TemplateName, DateTime StartedAt, DateTime EndedAt, long DurationMs,
    int ExerciseCount, int SetCount, double TotalVolume);

public record AdminWorkoutListResponse(IReadOnlyList<AdminWorkoutListItemDto> Items, int TotalCount, int Page, int PageSize);

public record AdminWorkoutDetailDto(
    string Id, string UserId, string Username, string? UserDisplayName, string UserEmail,
    string? TemplateName, DateTime StartedAt, DateTime EndedAt, long DurationMs,
    int ExerciseCount, int SetCount, double TotalVolume,
    JsonElement Logs, string? DeletedAt);

public record AdminBugReportListItemDto(
    string Id, string UserId, string Username, string? UserDisplayName,
    string Title, string Status, string Priority, string? Platform, string? AppVersion,
    bool HasScreenshot, DateTime CreatedAt);

public record AdminBugReportListResponse(IReadOnlyList<AdminBugReportListItemDto> Items, int TotalCount, int Page, int PageSize);

public record AdminBugReportDetailDto(
    string Id, string UserId, string Username, string? UserDisplayName, string UserEmail,
    string Title, string Description, string Status, string Priority,
    string? AppVersion, string? Platform, string? DeviceInfo, string? AppScreen, string? ErrorDetails,
    string? ScreenshotBase64, string? ScreenshotContentType, string? AdminNotes,
    DateTime CreatedAt, DateTime UpdatedAt);

public record UpdateBugReportRequest(string? Status, string? AdminNotes);

public record AnalyticsOverviewDto(
    int TotalUsers, int NewUsersInRange, int ActiveUsersInRange,
    int CompletedWorkoutsInRange, double AvgWorkoutsPerActiveUser);

public record DailyCountDto(DateOnly Date, int Count);

public record AnalyticsSeriesResponse(IReadOnlyList<DailyCountDto> Series);

public record TemplatePopularityDto(string TemplateName, int SessionCount);

public record AnalyticsTemplatePopularityResponse(IReadOnlyList<TemplatePopularityDto> Items);

public record AdminAuditLogItemDto(
    string Id, string AdminUserId, string AdminUsername, string Action,
    string TargetType, string? TargetId, string? Description, string? Metadata, DateTime CreatedAt);

public record AdminAuditLogListResponse(IReadOnlyList<AdminAuditLogItemDto> Items, int TotalCount, int Page, int PageSize);
