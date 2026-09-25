using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Controllers;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Tests;

// Direct-instantiation style (InMemory AppDbContext + controller called directly),
// matching WorkoutSessionDeletionAndHistoryTests.cs / AdminControllerTests.cs.
public class BugReportsControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly BugReportsController _controller;

    public BugReportsControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _db.Users.Add(new User { Id = _userId, Email = "reporter@example.com", Username = "reporter", PasswordHash = "x" });
        _db.SaveChanges();

        _controller = new BugReportsController(_db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = MakePrincipal(_userId) },
            },
        };
    }

    public void Dispose() => _db.Dispose();

    private static ClaimsPrincipal MakePrincipal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private static IFormFile MakeFile(string contentType, int sizeBytes, string fileName = "screenshot.jpg")
    {
        var stream = new MemoryStream(new byte[sizeBytes]);
        return new FormFile(stream, 0, stream.Length, "screenshot", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    [Fact]
    public async Task Create_WithoutScreenshot_CreatesReportScopedToCaller()
    {
        var result = await _controller.Create(
            "Crash on login", "App crashes when I tap sign in.",
            "1.2.3", "android", "Pixel 9, Android 15", "LoginScreen", null, null);

        var dto = Assert.IsType<BugReportDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Crash on login", dto.Title);
        Assert.Equal("Open", dto.Status);
        Assert.Null(dto.ScreenshotBase64);

        var stored = await _db.BugReports.SingleAsync();
        Assert.Equal(_userId, stored.UserId);
    }

    [Fact]
    public async Task Create_RejectsMissingTitle()
    {
        var result = await _controller.Create("  ", "desc", null, null, null, null, null, null);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.BugReports);
    }

    [Fact]
    public async Task Create_RejectsInvalidScreenshotContentType()
    {
        var file = MakeFile("application/x-msdownload", 1000, "payload.exe");
        var result = await _controller.Create("Title", "Desc", null, null, null, null, null, file);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.BugReports);
    }

    [Fact]
    public async Task Create_RejectsOversizedScreenshot()
    {
        var file = MakeFile("image/png", 6 * 1024 * 1024); // over the 5MB cap
        var result = await _controller.Create("Title", "Desc", null, null, null, null, null, file);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.BugReports);
    }

    [Fact]
    public async Task Create_WithValidScreenshot_StoresBase64AndContentType()
    {
        var file = MakeFile("image/png", 1024);
        var result = await _controller.Create("Title", "Desc", null, null, null, null, null, file);

        var dto = Assert.IsType<BugReportDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.NotNull(dto.ScreenshotBase64);
        Assert.Equal("image/png", dto.ScreenshotContentType);
    }

    [Fact]
    public async Task GetMine_ExcludesOtherUsersReportsAndOmitsScreenshotBytes()
    {
        var otherUserId = Guid.NewGuid();
        _db.Users.Add(new User { Id = otherUserId, Email = "other@example.com", Username = "other", PasswordHash = "x" });
        _db.BugReports.Add(new BugReport { UserId = _userId, Title = "Mine", Description = "d" });
        _db.BugReports.Add(new BugReport
        {
            UserId = otherUserId, Title = "Not mine", Description = "d",
            ScreenshotBase64 = "abc", ScreenshotContentType = "image/png",
        });
        await _db.SaveChangesAsync();

        var result = await _controller.GetMine();
        var items = Assert.IsAssignableFrom<IEnumerable<BugReportListItemDto>>(Assert.IsType<OkObjectResult>(result).Value).ToList();

        var item = Assert.Single(items);
        Assert.Equal("Mine", item.Title);
    }

    [Fact]
    public async Task GetMineDetail_ReturnsNotFoundForAnotherUsersReport()
    {
        var otherUserId = Guid.NewGuid();
        var report = new BugReport { UserId = otherUserId, Title = "Not mine", Description = "d" };
        _db.BugReports.Add(report);
        await _db.SaveChangesAsync();

        var result = await _controller.GetMineDetail(report.Id);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetMineDetail_ReturnsFullDetailForOwnReport()
    {
        var report = new BugReport
        {
            UserId = _userId, Title = "Mine", Description = "Full description",
            ScreenshotBase64 = "abc", ScreenshotContentType = "image/png",
        };
        _db.BugReports.Add(report);
        await _db.SaveChangesAsync();

        var result = await _controller.GetMineDetail(report.Id);
        var dto = Assert.IsType<BugReportDto>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal("Full description", dto.Description);
        Assert.Equal("abc", dto.ScreenshotBase64);
    }
}
