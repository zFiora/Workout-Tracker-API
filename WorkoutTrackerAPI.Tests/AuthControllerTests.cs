using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WorkoutTrackerAPI.Controllers;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;
using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Tests;

// Direct-instantiation style, matching AdminControllerTests.cs. Covers only the
// admin-login audit behavior added to AuthController.Login — the rest of AuthController
// is unrelated to this phase and already has no dedicated test file.
public class AuthControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-only-signing-key-not-used-anywhere-real-0123456789",
                ["Jwt:Issuer"] = "test-issuer",
                ["Jwt:Audience"] = "test-audience",
                ["Jwt:ExpiryDays"] = "1",
            })
            .Build();
        var jwt = new JwtService(config);
        var auditLog = new AuditLogService(_db);

        // PasswordResetService is never touched by Login() — the only action under
        // test here — so a null instance is safe; it exists only to satisfy the
        // constructor signature.
        _controller = new AuthController(_db, jwt, null!, auditLog);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Login_RecordsAuditLogForAdminUser()
    {
        var admin = new User
        {
            Email = "admin@example.com",
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            Role = UserRole.Admin,
        };
        _db.Users.Add(admin);
        await _db.SaveChangesAsync();

        await _controller.Login(new LoginRequest("admin@example.com", "Password123!"));

        var entry = Assert.Single(_db.AdminAuditLogs);
        Assert.Equal(admin.Id, entry.AdminUserId);
        Assert.Equal("admin", entry.AdminUsername);
        Assert.Equal("admin.login", entry.Action);
        Assert.Equal("User", entry.TargetType);
        Assert.Equal(admin.Id.ToString(), entry.TargetId);
    }

    [Fact]
    public async Task Login_DoesNotRecordAuditLogForPlainUser()
    {
        var user = new User
        {
            Email = "user@example.com",
            Username = "plainuser",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            Role = UserRole.User,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await _controller.Login(new LoginRequest("user@example.com", "Password123!"));

        Assert.Empty(_db.AdminAuditLogs);
    }

    [Fact]
    public async Task Login_DoesNotRecordAuditLogForFailedAttempt()
    {
        var admin = new User
        {
            Email = "admin2@example.com",
            Username = "admin2",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            Role = UserRole.Admin,
        };
        _db.Users.Add(admin);
        await _db.SaveChangesAsync();

        await _controller.Login(new LoginRequest("admin2@example.com", "WrongPassword!"));

        Assert.Empty(_db.AdminAuditLogs);
    }
}
