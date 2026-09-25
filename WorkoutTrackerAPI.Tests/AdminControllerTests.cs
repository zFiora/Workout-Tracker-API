using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Controllers;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;
using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Tests;

// Direct-instantiation style (InMemory AppDbContext + controller called directly, no
// WebApplicationFactory) — same pattern as WorkoutSessionDeletionAndHistoryTests.cs.
// This bypasses the [Authorize] pipeline entirely, so it only covers AdminController's
// business logic (dashboard math, search/sort/filter/pagination, detail DTO shape).
// 401/403 behavior is covered separately in AdminAuthorizationTests.cs, which does run
// the real middleware pipeline.
public class AdminControllerTests : IDisposable
{
    private const string AdminUsername = "testadmin";

    private readonly AppDbContext _db;
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly AdminController _controller;

    public AdminControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _controller = new AdminController(_db, new AuditLogService(_db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = MakePrincipal(_adminId, "Admin", AdminUsername) },
            },
        };
    }

    public void Dispose() => _db.Dispose();

    private static ClaimsPrincipal MakePrincipal(Guid userId, string role, string username = "testadmin") =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role),
                new Claim("username", username),
            ], "test"));

    private static User MakeUser(
        string email, string username, DateTime createdAt,
        DateTime? lastWorkoutDate = null, bool isActive = true, UserRole role = UserRole.User,
        int currentStreak = 0, int bestStreak = 0,
        DateTime? lastQualifyingWorkoutAt = null, string? timeZoneId = null) => new()
    {
        Email = email,
        Username = username,
        PasswordHash = "x",
        CreatedAt = createdAt,
        LastWorkoutDate = lastWorkoutDate,
        IsActive = isActive,
        Role = role,
        CurrentStreak = currentStreak,
        BestStreak = bestStreak,
        LastQualifyingWorkoutAt = lastQualifyingWorkoutAt,
        TimeZoneId = timeZoneId,
    };

    private WorkoutSession MakeSession(
        Guid userId, DateTime endedAt, DateTime? deletedAt = null,
        string? templateName = null, string logsJson = "[]") => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TemplateName = templateName,
        StartedAt = endedAt,
        EndedAt = endedAt,
        DurationMs = 1000,
        LogsJson = System.Text.Json.JsonDocument.Parse(logsJson),
        CreatedAtServer = endedAt,
        UpdatedAt = endedAt,
        DeletedAt = deletedAt,
    };

    // ---------- Dashboard ----------

    [Fact]
    public async Task Dashboard_BucketsUsersByCreatedAt()
    {
        var now = DateTime.UtcNow;
        var userToday = MakeUser("today@example.com", "today", now, lastWorkoutDate: now);
        var userThisWeek = MakeUser("week@example.com", "week", now.AddDays(-3));
        var userThisMonth = MakeUser("month@example.com", "month", now.AddDays(-20));
        var userOld = MakeUser("old@example.com", "old", now.AddDays(-100), lastWorkoutDate: now.AddDays(-100));
        _db.Users.AddRange(userToday, userThisWeek, userThisMonth, userOld);
        await _db.SaveChangesAsync();

        var result = await _controller.GetDashboard();
        var dto = Assert.IsType<AdminDashboardDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(4, dto.Users.Total);
        Assert.Equal(1, dto.Users.NewToday);
        Assert.Equal(2, dto.Users.NewThisWeek);
        Assert.Equal(3, dto.Users.NewThisMonth);
        Assert.Equal(1, dto.Users.Active); // only userToday's LastWorkoutDate is within 30 days
    }

    [Fact]
    public async Task Dashboard_ExcludesSoftDeletedSessionsFromCompletedCounts()
    {
        var now = DateTime.UtcNow;
        var user = MakeUser("u@example.com", "u", now);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.WorkoutSessions.Add(MakeSession(user.Id, now)); // active, today
        _db.WorkoutSessions.Add(MakeSession(user.Id, now, deletedAt: now)); // tombstoned, must be excluded
        await _db.SaveChangesAsync();

        var result = await _controller.GetDashboard();
        var dto = Assert.IsType<AdminDashboardDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(1, dto.Workouts.TotalCompleted);
        Assert.Equal(1, dto.Workouts.Today);
    }

    // ---------- GetUsers: search / sort / filter / pagination ----------

    [Fact]
    public async Task GetUsers_SearchMatchesEmailUsernameOrDisplayNameCaseInsensitively()
    {
        var now = DateTime.UtcNow;
        var carol = MakeUser("carol@example.com", "carol", now);
        carol.DisplayName = "Special Nickname";
        _db.Users.AddRange(
            MakeUser("alice@example.com", "alice", now),
            MakeUser("bob@example.com", "bobby", now),
            carol);
        await _db.SaveChangesAsync();

        var result = await _controller.GetUsers(search: "ALICE", sortBy: "createdAt", sortDir: "desc", status: "all", page: 1, pageSize: 20);
        var dto = Assert.IsType<AdminUserListResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Single(dto.Items);
        Assert.Equal("alice", dto.Items[0].Username);

        var byNickname = await _controller.GetUsers(search: "nickname", sortBy: "createdAt", sortDir: "desc", status: "all", page: 1, pageSize: 20);
        var nicknameDto = Assert.IsType<AdminUserListResponse>(Assert.IsType<OkObjectResult>(byNickname).Value);
        Assert.Single(nicknameDto.Items);
        Assert.Equal("carol", nicknameDto.Items[0].Username);
    }

    [Fact]
    public async Task GetUsers_FiltersByStatus()
    {
        var now = DateTime.UtcNow;
        _db.Users.AddRange(
            MakeUser("active@example.com", "active", now, isActive: true),
            MakeUser("suspended@example.com", "suspended", now, isActive: false));
        await _db.SaveChangesAsync();

        var activeResult = await _controller.GetUsers(search: null, status: "active");
        var activeDto = Assert.IsType<AdminUserListResponse>(Assert.IsType<OkObjectResult>(activeResult).Value);
        Assert.Single(activeDto.Items);
        Assert.Equal("active", activeDto.Items[0].Username);

        var suspendedResult = await _controller.GetUsers(search: null, status: "suspended");
        var suspendedDto = Assert.IsType<AdminUserListResponse>(Assert.IsType<OkObjectResult>(suspendedResult).Value);
        Assert.Single(suspendedDto.Items);
        Assert.Equal("suspended", suspendedDto.Items[0].Username);
    }

    [Fact]
    public async Task GetUsers_SortsByUsernameAscending()
    {
        var now = DateTime.UtcNow;
        _db.Users.AddRange(
            MakeUser("c@example.com", "charlie", now),
            MakeUser("a@example.com", "alpha", now),
            MakeUser("b@example.com", "bravo", now));
        await _db.SaveChangesAsync();

        var result = await _controller.GetUsers(search: null, sortBy: "username", sortDir: "asc");
        var dto = Assert.IsType<AdminUserListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(["alpha", "bravo", "charlie"], dto.Items.Select(u => u.Username));
    }

    [Fact]
    public async Task GetUsers_PaginatesAndReportsTotalCountAgainstUnpagedSet()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            _db.Users.Add(MakeUser($"user{i}@example.com", $"user{i}", now));
        await _db.SaveChangesAsync();

        var page1 = await _controller.GetUsers(search: null, page: 1, pageSize: 2);
        var page1Dto = Assert.IsType<AdminUserListResponse>(Assert.IsType<OkObjectResult>(page1).Value);
        Assert.Equal(2, page1Dto.Items.Count);
        Assert.Equal(5, page1Dto.TotalCount);

        var page3 = await _controller.GetUsers(search: null, page: 3, pageSize: 2);
        var page3Dto = Assert.IsType<AdminUserListResponse>(Assert.IsType<OkObjectResult>(page3).Value);
        Assert.Single(page3Dto.Items); // 5 total, page size 2 -> last page has 1
    }

    // ---------- GetUser (detail) ----------

    [Fact]
    public async Task GetUser_ReturnsNotFoundForUnknownId()
    {
        var result = await _controller.GetUser(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetUser_NullMacroProfileFieldsWhenAbsent()
    {
        var user = MakeUser("solo@example.com", "solo", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _controller.GetUser(user.Id);
        var dto = Assert.IsType<AdminUserDetailDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Null(dto.Sex);
        Assert.Null(dto.DateOfBirth);
        Assert.Null(dto.HeightCm);
    }

    [Fact]
    public async Task GetUser_PopulatesMacroProfileFieldsWhenPresent()
    {
        var user = MakeUser("withprofile@example.com", "withprofile", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.MacroProfiles.Add(new MacroProfile
        {
            UserId = user.Id,
            Sex = "female",
            DateOfBirth = new DateOnly(1995, 3, 14),
            HeightCm = 168.5,
        });
        await _db.SaveChangesAsync();

        var result = await _controller.GetUser(user.Id);
        var dto = Assert.IsType<AdminUserDetailDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal("female", dto.Sex);
        Assert.Equal(new DateOnly(1995, 3, 14), dto.DateOfBirth);
        Assert.Equal(168.5, dto.HeightCm);
    }

    [Fact]
    public async Task GetUser_CountsAreScopedToTheRequestedUserOnly()
    {
        var target = MakeUser("target@example.com", "target", DateTime.UtcNow);
        var other = MakeUser("other@example.com", "other", DateTime.UtcNow);
        _db.Users.AddRange(target, other);
        await _db.SaveChangesAsync();

        _db.WorkoutSessions.Add(MakeSession(target.Id, DateTime.UtcNow));
        _db.WorkoutSessions.Add(MakeSession(other.Id, DateTime.UtcNow));
        _db.WorkoutSessions.Add(MakeSession(other.Id, DateTime.UtcNow));
        await _db.SaveChangesAsync();

        var result = await _controller.GetUser(target.Id);
        var dto = Assert.IsType<AdminUserDetailDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(1, dto.WorkoutCount);
    }

    [Fact]
    public async Task GetUser_UsesEffectiveCurrentStreakNotRawColumn()
    {
        // Raw CurrentStreak says 10, but LastQualifyingWorkoutAt is stale (10 days ago),
        // so the freshness-checked effective streak must report 0, not the raw column.
        var user = MakeUser(
            "stale@example.com", "stale", DateTime.UtcNow,
            currentStreak: 10, lastQualifyingWorkoutAt: DateTime.UtcNow.AddDays(-10));
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _controller.GetUser(user.Id);
        var dto = Assert.IsType<AdminUserDetailDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(0, dto.CurrentStreak);
    }

    // ---------- SetStatus ----------

    [Fact]
    public async Task SetStatus_TogglesIsActiveAndPersists()
    {
        var user = MakeUser("tobesuspended@example.com", "tobesuspended", DateTime.UtcNow, isActive: true);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _controller.SetStatus(user.Id, new SetUserStatusRequest(false));
        var dto = Assert.IsType<AdminUserListItemDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(dto.IsActive);

        var reloaded = await _db.Users.FindAsync(user.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task SetStatus_ReturnsNotFoundForUnknownId()
    {
        var result = await _controller.SetStatus(Guid.NewGuid(), new SetUserStatusRequest(false));
        Assert.IsType<NotFoundResult>(result);
    }

    // ---------- GetWorkouts / GetWorkout ----------

    private const string TwoExerciseLogsJson = """
        [
          { "exerciseId": 1, "exerciseName": "Bench Press", "sets": [
              { "weight": 100, "reps": 5, "type": "work" },
              { "weight": 100, "reps": 5, "type": "work" }
            ] },
          { "exerciseId": 2, "exerciseName": "Squat", "sets": [
              { "weight": 140, "reps": 3, "type": "work" }
            ] }
        ]
        """;

    [Fact]
    public async Task GetWorkouts_ExcludesSoftDeletedSessions()
    {
        var user = MakeUser("w1@example.com", "w1", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.WorkoutSessions.Add(MakeSession(user.Id, DateTime.UtcNow));
        _db.WorkoutSessions.Add(MakeSession(user.Id, DateTime.UtcNow, deletedAt: DateTime.UtcNow));
        await _db.SaveChangesAsync();

        var result = await _controller.GetWorkouts(null, null, null, null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminWorkoutListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Single(dto.Items);
    }

    [Fact]
    public async Task GetWorkouts_FiltersByUserId()
    {
        var a = MakeUser("a@example.com", "aaa", DateTime.UtcNow);
        var b = MakeUser("b@example.com", "bbb", DateTime.UtcNow);
        _db.Users.AddRange(a, b);
        await _db.SaveChangesAsync();

        _db.WorkoutSessions.Add(MakeSession(a.Id, DateTime.UtcNow));
        _db.WorkoutSessions.Add(MakeSession(b.Id, DateTime.UtcNow));
        _db.WorkoutSessions.Add(MakeSession(b.Id, DateTime.UtcNow));
        await _db.SaveChangesAsync();

        var result = await _controller.GetWorkouts(b.Id, null, null, null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminWorkoutListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, dto.TotalCount);
        Assert.All(dto.Items, i => Assert.Equal(b.Id.ToString(), i.UserId));
    }

    [Fact]
    public async Task GetWorkouts_UserSearchMatchesUsernameEmailOrDisplayName()
    {
        var target = MakeUser("findme@example.com", "findme", DateTime.UtcNow);
        var other = MakeUser("other@example.com", "other", DateTime.UtcNow);
        _db.Users.AddRange(target, other);
        await _db.SaveChangesAsync();

        _db.WorkoutSessions.Add(MakeSession(target.Id, DateTime.UtcNow));
        _db.WorkoutSessions.Add(MakeSession(other.Id, DateTime.UtcNow));
        await _db.SaveChangesAsync();

        var result = await _controller.GetWorkouts(null, "FINDME", null, null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminWorkoutListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Single(dto.Items);
        Assert.Equal("findme", dto.Items[0].Username);
    }

    [Fact]
    public async Task GetWorkouts_FiltersByTemplateName()
    {
        var user = MakeUser("t@example.com", "t", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.WorkoutSessions.Add(MakeSession(user.Id, DateTime.UtcNow, templateName: "Push Day"));
        _db.WorkoutSessions.Add(MakeSession(user.Id, DateTime.UtcNow, templateName: "Leg Day"));
        await _db.SaveChangesAsync();

        var result = await _controller.GetWorkouts(null, null, "push", null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminWorkoutListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Single(dto.Items);
        Assert.Equal("Push Day", dto.Items[0].TemplateName);
    }

    [Fact]
    public async Task GetWorkouts_FiltersByDateRange()
    {
        var user = MakeUser("d@example.com", "d", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var inRange = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var beforeRange = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var afterRange = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        _db.WorkoutSessions.Add(MakeSession(user.Id, inRange));
        _db.WorkoutSessions.Add(MakeSession(user.Id, beforeRange));
        _db.WorkoutSessions.Add(MakeSession(user.Id, afterRange));
        await _db.SaveChangesAsync();

        var result = await _controller.GetWorkouts(
            null, null, null,
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            "desc", 1, 20);
        var dto = Assert.IsType<AdminWorkoutListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Single(dto.Items);
    }

    [Fact]
    public async Task GetWorkouts_SortsByEndedAtAscendingAndDescending()
    {
        var user = MakeUser("sort@example.com", "sort", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var earlier = DateTime.UtcNow.AddDays(-2);
        var later = DateTime.UtcNow.AddDays(-1);
        _db.WorkoutSessions.Add(MakeSession(user.Id, later, templateName: "Later"));
        _db.WorkoutSessions.Add(MakeSession(user.Id, earlier, templateName: "Earlier"));
        await _db.SaveChangesAsync();

        var asc = await _controller.GetWorkouts(null, null, null, null, null, "asc", 1, 20);
        var ascDto = Assert.IsType<AdminWorkoutListResponse>(Assert.IsType<OkObjectResult>(asc).Value);
        Assert.Equal(["Earlier", "Later"], ascDto.Items.Select(i => i.TemplateName));

        var desc = await _controller.GetWorkouts(null, null, null, null, null, "desc", 1, 20);
        var descDto = Assert.IsType<AdminWorkoutListResponse>(Assert.IsType<OkObjectResult>(desc).Value);
        Assert.Equal(["Later", "Earlier"], descDto.Items.Select(i => i.TemplateName));
    }

    [Fact]
    public async Task GetWorkouts_PaginatesAndReportsTotalCountAgainstUnpagedSet()
    {
        var user = MakeUser("page@example.com", "page", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        for (var i = 0; i < 5; i++)
            _db.WorkoutSessions.Add(MakeSession(user.Id, DateTime.UtcNow.AddMinutes(-i)));
        await _db.SaveChangesAsync();

        var page1 = await _controller.GetWorkouts(null, null, null, null, null, "desc", 1, 2);
        var page1Dto = Assert.IsType<AdminWorkoutListResponse>(Assert.IsType<OkObjectResult>(page1).Value);
        Assert.Equal(2, page1Dto.Items.Count);
        Assert.Equal(5, page1Dto.TotalCount);
    }

    [Fact]
    public async Task GetWorkouts_ComputesExerciseSetCountAndVolumeFromLogsJson()
    {
        var user = MakeUser("stats@example.com", "stats", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.WorkoutSessions.Add(MakeSession(user.Id, DateTime.UtcNow, logsJson: TwoExerciseLogsJson));
        await _db.SaveChangesAsync();

        var result = await _controller.GetWorkouts(null, null, null, null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminWorkoutListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        var item = Assert.Single(dto.Items);
        Assert.Equal(2, item.ExerciseCount); // Bench Press, Squat
        Assert.Equal(3, item.SetCount); // 2 bench sets + 1 squat set
        Assert.Equal(100 * 5 + 100 * 5 + 140 * 3, item.TotalVolume);
    }

    [Fact]
    public async Task GetWorkout_ReturnsNotFoundForUnknownId()
    {
        var result = await _controller.GetWorkout(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetWorkout_ReturnsFullDetailWithOwnerInfoAndRawLogs()
    {
        var user = MakeUser("detail@example.com", "detailuser", DateTime.UtcNow);
        user.DisplayName = "Detail User";
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var session = MakeSession(user.Id, DateTime.UtcNow, templateName: "Push Day", logsJson: TwoExerciseLogsJson);
        _db.WorkoutSessions.Add(session);
        await _db.SaveChangesAsync();

        var result = await _controller.GetWorkout(session.Id);
        var dto = Assert.IsType<AdminWorkoutDetailDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal("detailuser", dto.Username);
        Assert.Equal("Detail User", dto.UserDisplayName);
        Assert.Equal("detail@example.com", dto.UserEmail);
        Assert.Equal("Push Day", dto.TemplateName);
        Assert.Equal(2, dto.ExerciseCount);
        Assert.Equal(3, dto.SetCount);
        Assert.Equal(2, dto.Logs.GetArrayLength());
        Assert.Null(dto.DeletedAt);
    }

    // ---------- GetBugReports / GetBugReport / UpdateBugReport ----------

    [Fact]
    public async Task GetBugReports_FiltersByStatus()
    {
        var user = MakeUser("br1@example.com", "br1", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.BugReports.Add(new BugReport { UserId = user.Id, Title = "Open one", Description = "d", Status = BugReportStatus.Open });
        _db.BugReports.Add(new BugReport { UserId = user.Id, Title = "Closed one", Description = "d", Status = BugReportStatus.Closed });
        await _db.SaveChangesAsync();

        var result = await _controller.GetBugReports("open", null, null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminBugReportListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        var item = Assert.Single(dto.Items);
        Assert.Equal("Open one", item.Title);
    }

    [Fact]
    public async Task GetBugReports_UserSearchMatchesReporterUsernameEmailOrDisplayName()
    {
        var target = MakeUser("findme2@example.com", "findme2", DateTime.UtcNow);
        var other = MakeUser("other2@example.com", "other2", DateTime.UtcNow);
        _db.Users.AddRange(target, other);
        await _db.SaveChangesAsync();

        _db.BugReports.Add(new BugReport { UserId = target.Id, Title = "From target", Description = "d" });
        _db.BugReports.Add(new BugReport { UserId = other.Id, Title = "From other", Description = "d" });
        await _db.SaveChangesAsync();

        var result = await _controller.GetBugReports(null, "FINDME2", null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminBugReportListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        var item = Assert.Single(dto.Items);
        Assert.Equal("From target", item.Title);
    }

    [Fact]
    public async Task GetBugReports_FiltersByDateRangeAndPaginates()
    {
        var user = MakeUser("br2@example.com", "br2", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.BugReports.Add(new BugReport
        {
            UserId = user.Id, Title = "In range", Description = "d",
            CreatedAt = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc),
        });
        _db.BugReports.Add(new BugReport
        {
            UserId = user.Id, Title = "Out of range", Description = "d",
            CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        await _db.SaveChangesAsync();

        var result = await _controller.GetBugReports(
            null, null, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), "desc", 1, 20);
        var dto = Assert.IsType<AdminBugReportListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Single(dto.Items);
        Assert.Equal(1, dto.TotalCount);
    }

    [Fact]
    public async Task GetBugReport_ReturnsNotFoundForUnknownId()
    {
        var result = await _controller.GetBugReport(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetBugReport_IncludesAdminNotesAndScreenshotAndReporterInfo()
    {
        var user = MakeUser("br3@example.com", "br3", DateTime.UtcNow);
        user.DisplayName = "BR Three";
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var report = new BugReport
        {
            UserId = user.Id, Title = "T", Description = "D",
            ScreenshotBase64 = "abc", ScreenshotContentType = "image/png",
            AdminNotes = "Investigating on staging",
        };
        _db.BugReports.Add(report);
        await _db.SaveChangesAsync();

        var result = await _controller.GetBugReport(report.Id);
        var dto = Assert.IsType<AdminBugReportDetailDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal("br3", dto.Username);
        Assert.Equal("BR Three", dto.UserDisplayName);
        Assert.Equal("Investigating on staging", dto.AdminNotes);
        Assert.Equal("abc", dto.ScreenshotBase64);
    }

    [Fact]
    public async Task UpdateBugReport_UpdatesStatusAndNotesIndependentlyAndBumpsUpdatedAt()
    {
        var user = MakeUser("br4@example.com", "br4", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var report = new BugReport { UserId = user.Id, Title = "T", Description = "D" };
        _db.BugReports.Add(report);
        await _db.SaveChangesAsync();
        var originalUpdatedAt = report.UpdatedAt;

        await Task.Delay(10);
        var statusResult = await _controller.UpdateBugReport(report.Id, new UpdateBugReportRequest("InProgress", null));
        var statusDto = Assert.IsType<AdminBugReportDetailDto>(Assert.IsType<OkObjectResult>(statusResult).Value);
        Assert.Equal("InProgress", statusDto.Status);
        Assert.True(statusDto.UpdatedAt > originalUpdatedAt);

        var notesResult = await _controller.UpdateBugReport(report.Id, new UpdateBugReportRequest(null, "Reached out to user"));
        var notesDto = Assert.IsType<AdminBugReportDetailDto>(Assert.IsType<OkObjectResult>(notesResult).Value);
        Assert.Equal("InProgress", notesDto.Status); // unchanged by the notes-only update
        Assert.Equal("Reached out to user", notesDto.AdminNotes);
    }

    [Fact]
    public async Task UpdateBugReport_RejectsInvalidStatus()
    {
        var user = MakeUser("br5@example.com", "br5", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var report = new BugReport { UserId = user.Id, Title = "T", Description = "D" };
        _db.BugReports.Add(report);
        await _db.SaveChangesAsync();

        var result = await _controller.UpdateBugReport(report.Id, new UpdateBugReportRequest("NotARealStatus", null));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateBugReport_ReturnsNotFoundForUnknownId()
    {
        var result = await _controller.UpdateBugReport(Guid.NewGuid(), new UpdateBugReportRequest("Closed", null));
        Assert.IsType<NotFoundResult>(result);
    }

    // ---------- Analytics ----------

    [Fact]
    public async Task GetAnalyticsOverview_ComputesRangeScopedTotals()
    {
        var inRangeUser = MakeUser("a1@example.com", "a1", new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        var outOfRangeUser = MakeUser("a2@example.com", "a2", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _db.Users.AddRange(inRangeUser, outOfRangeUser);
        await _db.SaveChangesAsync();

        _db.WorkoutSessions.Add(MakeSession(inRangeUser.Id, new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc)));
        _db.WorkoutSessions.Add(MakeSession(inRangeUser.Id, new DateTime(2026, 6, 13, 0, 0, 0, DateTimeKind.Utc)));
        await _db.SaveChangesAsync();

        var result = await _controller.GetAnalyticsOverview(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        var dto = Assert.IsType<AnalyticsOverviewDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, dto.TotalUsers);
        Assert.Equal(1, dto.NewUsersInRange);
        Assert.Equal(1, dto.ActiveUsersInRange);
        Assert.Equal(2, dto.CompletedWorkoutsInRange);
        Assert.Equal(2.0, dto.AvgWorkoutsPerActiveUser);
    }

    [Fact]
    public async Task GetUserGrowth_ProducesContinuousSeriesIncludingZeroDays()
    {
        var user = MakeUser("g1@example.com", "g1", new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _controller.GetUserGrowth(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 5));
        var dto = Assert.IsType<AnalyticsSeriesResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(5, dto.Series.Count); // June 1 through June 5 inclusive
        Assert.Equal(0, dto.Series[0].Count);
        Assert.Equal(1, dto.Series[^1].Count);
        Assert.Equal(new DateOnly(2026, 6, 1), dto.Series[0].Date);
        Assert.Equal(new DateOnly(2026, 6, 5), dto.Series[^1].Date);
    }

    [Fact]
    public async Task GetActiveUsersSeries_CountsDistinctUsersNotSessions()
    {
        var user = MakeUser("dau@example.com", "dau", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var day = new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc);
        _db.WorkoutSessions.Add(MakeSession(user.Id, day));
        _db.WorkoutSessions.Add(MakeSession(user.Id, day.AddHours(4))); // same user, same day, 2nd session
        await _db.SaveChangesAsync();

        var activeResult = await _controller.GetActiveUsersSeries(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 10));
        var activeDto = Assert.IsType<AnalyticsSeriesResponse>(Assert.IsType<OkObjectResult>(activeResult).Value);
        Assert.Equal(1, Assert.Single(activeDto.Series).Count); // one distinct user

        var activityResult = await _controller.GetWorkoutActivitySeries(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 10));
        var activityDto = Assert.IsType<AnalyticsSeriesResponse>(Assert.IsType<OkObjectResult>(activityResult).Value);
        Assert.Equal(2, Assert.Single(activityDto.Series).Count); // two sessions
    }

    [Fact]
    public async Task GetWorkoutActivitySeries_ExcludesSoftDeletedSessions()
    {
        var user = MakeUser("del@example.com", "del", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var day = new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc);
        _db.WorkoutSessions.Add(MakeSession(user.Id, day));
        _db.WorkoutSessions.Add(MakeSession(user.Id, day, deletedAt: day));
        await _db.SaveChangesAsync();

        var result = await _controller.GetWorkoutActivitySeries(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 10));
        var dto = Assert.IsType<AnalyticsSeriesResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(1, Assert.Single(dto.Series).Count);
    }

    [Fact]
    public async Task GetTemplatePopularity_RanksAndBucketsNullTemplateNameAsAdHoc()
    {
        var user = MakeUser("pop@example.com", "pop", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        _db.WorkoutSessions.Add(MakeSession(user.Id, now, templateName: "Push Day"));
        _db.WorkoutSessions.Add(MakeSession(user.Id, now, templateName: "Push Day"));
        _db.WorkoutSessions.Add(MakeSession(user.Id, now, templateName: "Leg Day"));
        _db.WorkoutSessions.Add(MakeSession(user.Id, now, templateName: null));
        await _db.SaveChangesAsync();

        var result = await _controller.GetTemplatePopularity(null, null, 10);
        var dto = Assert.IsType<AnalyticsTemplatePopularityResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal("Push Day", dto.Items[0].TemplateName);
        Assert.Equal(2, dto.Items[0].SessionCount);
        Assert.Contains(dto.Items, i => i.TemplateName == "Ad-hoc workout" && i.SessionCount == 1);
    }

    [Fact]
    public async Task GetTemplatePopularity_RespectsLimit()
    {
        var user = MakeUser("lim@example.com", "lim", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            _db.WorkoutSessions.Add(MakeSession(user.Id, now, templateName: $"Template {i}"));
        await _db.SaveChangesAsync();

        var result = await _controller.GetTemplatePopularity(null, null, 2);
        var dto = Assert.IsType<AnalyticsTemplatePopularityResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, dto.Items.Count);
    }

    // ---------- Audit logging: SetStatus / UpdateBugReport ----------

    [Fact]
    public async Task SetStatus_Suspend_RecordsAuditLogWithCorrectActorActionAndTarget()
    {
        var user = MakeUser("suspendme@example.com", "suspendme", DateTime.UtcNow, isActive: true);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await _controller.SetStatus(user.Id, new SetUserStatusRequest(false));

        var entry = Assert.Single(_db.AdminAuditLogs);
        Assert.Equal(_adminId, entry.AdminUserId);
        Assert.Equal(AdminUsername, entry.AdminUsername);
        Assert.Equal("user.suspend", entry.Action);
        Assert.Equal("User", entry.TargetType);
        Assert.Equal(user.Id.ToString(), entry.TargetId);
        Assert.Contains("Suspended", entry.Description);
        Assert.Contains("suspendme", entry.Description);
    }

    [Fact]
    public async Task SetStatus_Reactivate_RecordsAuditLogEntry()
    {
        var user = MakeUser("reactivateme@example.com", "reactivateme", DateTime.UtcNow, isActive: false);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await _controller.SetStatus(user.Id, new SetUserStatusRequest(true));

        var entry = Assert.Single(_db.AdminAuditLogs);
        Assert.Equal("user.reactivate", entry.Action);
        Assert.Contains("Reactivated", entry.Description);
    }

    [Fact]
    public async Task UpdateBugReport_StatusChange_RecordsAuditLogEntry()
    {
        var user = MakeUser("reporter@example.com", "reporter", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var report = new BugReport { UserId = user.Id, Title = "Crash", Description = "d", Status = BugReportStatus.Open };
        _db.BugReports.Add(report);
        await _db.SaveChangesAsync();

        await _controller.UpdateBugReport(report.Id, new UpdateBugReportRequest("Resolved", null));

        var entry = Assert.Single(_db.AdminAuditLogs);
        Assert.Equal(_adminId, entry.AdminUserId);
        Assert.Equal("bugreport.status_change", entry.Action);
        Assert.Equal("BugReport", entry.TargetType);
        Assert.Equal(report.Id.ToString(), entry.TargetId);
        Assert.Contains("Open", entry.Metadata);
        Assert.Contains("Resolved", entry.Metadata);
    }

    [Fact]
    public async Task UpdateBugReport_NotesOnlyUpdate_DoesNotRecordAuditLogEntry()
    {
        var user = MakeUser("reporter2@example.com", "reporter2", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var report = new BugReport { UserId = user.Id, Title = "Crash", Description = "d", Status = BugReportStatus.Open };
        _db.BugReports.Add(report);
        await _db.SaveChangesAsync();

        await _controller.UpdateBugReport(report.Id, new UpdateBugReportRequest(null, "Just some internal notes"));

        Assert.Empty(_db.AdminAuditLogs);
    }

    [Fact]
    public async Task UpdateBugReport_StatusUnchanged_DoesNotRecordAuditLogEntry()
    {
        var user = MakeUser("reporter3@example.com", "reporter3", DateTime.UtcNow);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var report = new BugReport { UserId = user.Id, Title = "Crash", Description = "d", Status = BugReportStatus.Open };
        _db.BugReports.Add(report);
        await _db.SaveChangesAsync();

        await _controller.UpdateBugReport(report.Id, new UpdateBugReportRequest("Open", null)); // same status

        Assert.Empty(_db.AdminAuditLogs);
    }

    [Fact]
    public async Task AuditLog_NeverContainsPasswordHashEvenWhenTargetUserHasOne()
    {
        var fakeHash = "$2a$11$Th1sLooksLikeARealBcryptHashValue1234567890";
        var user = new User
        {
            Email = "secret@example.com", Username = "secretuser", PasswordHash = fakeHash, IsActive = true,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await _controller.SetStatus(user.Id, new SetUserStatusRequest(false));

        var entry = Assert.Single(_db.AdminAuditLogs);
        Assert.DoesNotContain(fakeHash, entry.Description ?? "");
        Assert.DoesNotContain(fakeHash, entry.Metadata ?? "");
        Assert.DoesNotContain("password", (entry.Description ?? "").ToLower());
        Assert.DoesNotContain("password", (entry.Metadata ?? "").ToLower());
    }

    // ---------- GetAuditLogs ----------

    [Fact]
    public async Task GetAuditLogs_OrdersNewestFirstByDefault()
    {
        _db.AdminAuditLogs.AddRange(
            new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "a", TargetType = "T", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "b", TargetType = "T", CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
            new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "c", TargetType = "T", CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
        await _db.SaveChangesAsync();

        var result = await _controller.GetAuditLogs(null, null, null, null, null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminAuditLogListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(["b", "c", "a"], dto.Items.Select(i => i.Action));
    }

    [Fact]
    public async Task GetAuditLogs_FiltersByAction()
    {
        _db.AdminAuditLogs.AddRange(
            new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "user.suspend", TargetType = "User" },
            new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "user.reactivate", TargetType = "User" });
        await _db.SaveChangesAsync();

        var result = await _controller.GetAuditLogs("user.suspend", null, null, null, null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminAuditLogListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        var item = Assert.Single(dto.Items);
        Assert.Equal("user.suspend", item.Action);
    }

    [Fact]
    public async Task GetAuditLogs_FiltersByAdminSearch()
    {
        _db.AdminAuditLogs.AddRange(
            new AdminAuditLog { AdminUserId = Guid.NewGuid(), AdminUsername = "alice", Action = "a", TargetType = "T" },
            new AdminAuditLog { AdminUserId = Guid.NewGuid(), AdminUsername = "bob", Action = "a", TargetType = "T" });
        await _db.SaveChangesAsync();

        var result = await _controller.GetAuditLogs(null, "ALICE", null, null, null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminAuditLogListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        var item = Assert.Single(dto.Items);
        Assert.Equal("alice", item.AdminUsername);
    }

    [Fact]
    public async Task GetAuditLogs_FiltersByTargetTypeAndTargetId()
    {
        var targetId = Guid.NewGuid().ToString();
        _db.AdminAuditLogs.AddRange(
            new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "a", TargetType = "User", TargetId = targetId },
            new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "a", TargetType = "BugReport", TargetId = Guid.NewGuid().ToString() });
        await _db.SaveChangesAsync();

        var result = await _controller.GetAuditLogs(null, null, "User", targetId, null, null, "desc", 1, 20);
        var dto = Assert.IsType<AdminAuditLogListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        var item = Assert.Single(dto.Items);
        Assert.Equal("User", item.TargetType);
        Assert.Equal(targetId, item.TargetId);
    }

    [Fact]
    public async Task GetAuditLogs_FiltersByDateRange()
    {
        _db.AdminAuditLogs.AddRange(
            new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "a", TargetType = "T", CreatedAt = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc) },
            new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "a", TargetType = "T", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        await _db.SaveChangesAsync();

        var result = await _controller.GetAuditLogs(
            null, null, null, null, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), "desc", 1, 20);
        var dto = Assert.IsType<AdminAuditLogListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Single(dto.Items);
    }

    [Fact]
    public async Task GetAuditLogs_PaginatesAndReportsTotalCountAgainstUnpagedSet()
    {
        for (var i = 0; i < 5; i++)
            _db.AdminAuditLogs.Add(new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "a", TargetType = "T" });
        await _db.SaveChangesAsync();

        var page1 = await _controller.GetAuditLogs(null, null, null, null, null, null, "desc", 1, 2);
        var page1Dto = Assert.IsType<AdminAuditLogListResponse>(Assert.IsType<OkObjectResult>(page1).Value);

        Assert.Equal(2, page1Dto.Items.Count);
        Assert.Equal(5, page1Dto.TotalCount);
    }

    [Fact]
    public async Task GetAuditLogs_ClampsPageSizeToMaximum()
    {
        for (var i = 0; i < 3; i++)
            _db.AdminAuditLogs.Add(new AdminAuditLog { AdminUserId = _adminId, AdminUsername = AdminUsername, Action = "a", TargetType = "T" });
        await _db.SaveChangesAsync();

        var result = await _controller.GetAuditLogs(null, null, null, null, null, null, "desc", 1, 10_000);
        var dto = Assert.IsType<AdminAuditLogListResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(100, dto.PageSize); // clamped, not the requested 10,000
    }
}
