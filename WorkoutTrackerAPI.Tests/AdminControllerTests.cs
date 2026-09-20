using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Controllers;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Tests;

// Direct-instantiation style (InMemory AppDbContext + controller called directly, no
// WebApplicationFactory) — same pattern as WorkoutSessionDeletionAndHistoryTests.cs.
// This bypasses the [Authorize] pipeline entirely, so it only covers AdminController's
// business logic (dashboard math, search/sort/filter/pagination, detail DTO shape).
// 401/403 behavior is covered separately in AdminAuthorizationTests.cs, which does run
// the real middleware pipeline.
public class AdminControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AdminController _controller;

    public AdminControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _controller = new AdminController(_db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = MakePrincipal(Guid.NewGuid(), "Admin") },
            },
        };
    }

    public void Dispose() => _db.Dispose();

    private static ClaimsPrincipal MakePrincipal(Guid userId, string role) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role),
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

    private WorkoutSession MakeSession(Guid userId, DateTime endedAt, DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        StartedAt = endedAt,
        EndedAt = endedAt,
        DurationMs = 1000,
        LogsJson = System.Text.Json.JsonDocument.Parse("[]"),
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
}
