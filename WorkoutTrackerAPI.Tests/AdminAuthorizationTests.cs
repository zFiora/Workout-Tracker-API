using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;
using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Tests;

// The one test file in this project that needs a real HTTP pipeline: [Authorize(Roles =
// "Admin")] is enforced by ASP.NET's authorization *middleware*, which the direct
// controller-instantiation style used elsewhere (AdminControllerTests.cs,
// WorkoutSessionDeletionAndHistoryTests.cs) never exercises. Everything else about
// AdminController's behavior is covered there; this file only asserts 401/403/200.
public class AdminAuthorizationTests : IAsyncLifetime
{
    // Program.cs reads Jwt:Key/Issuer/Audience into a plain string via
    // builder.Configuration["Jwt:Key"] the moment that line executes — eagerly,
    // before WebApplicationFactory's ConfigureWebHost overrides are applied (those
    // only affect config sources added before the deferred Build() call, which is
    // too late for a value already captured into a local variable). Environment
    // variables, by contrast, are read by WebApplication.CreateBuilder(args) itself
    // at the very start, so they're visible in time. Set as a static constructor so
    // they exist before any instance (and its WorkoutTrackerApiFactory field) is built.
    static AdminAuthorizationTests()
    {
        Environment.SetEnvironmentVariable("Jwt__Key", "test-only-signing-key-not-used-anywhere-real-0123456789");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "test-issuer");
        Environment.SetEnvironmentVariable("Jwt__Audience", "test-audience");
        Environment.SetEnvironmentVariable("Jwt__ExpiryDays", "1");
    }

    private readonly WorkoutTrackerApiFactory _factory = new();
    private Guid _adminUserId;
    private Guid _plainUserId;

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var admin = new User
        {
            Email = "admin@example.com",
            Username = "admin",
            PasswordHash = "x",
            Role = UserRole.Admin,
        };
        var plain = new User
        {
            Email = "plain@example.com",
            Username = "plain",
            PasswordHash = "x",
            Role = UserRole.User,
        };
        db.Users.AddRange(admin, plain);
        await db.SaveChangesAsync();

        _adminUserId = admin.Id;
        _plainUserId = plain.Id;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> TokenForAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtService>();
        var user = await db.Users.FindAsync(userId);
        return jwt.Generate(user!);
    }

    [Fact]
    public async Task AdminToken_GetsDashboard200()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await TokenForAsync(_adminUserId));

        var response = await client.GetAsync("/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NonAdminToken_GetsDashboard403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await TokenForAsync(_plainUserId));

        var response = await client.GetAsync("/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_GetsDashboard401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private class WorkoutTrackerApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // Remove the real Npgsql-backed factory/context registrations and
                // replace with InMemory, mirroring Program.cs's one-registration
                // pattern (AddDbContextFactory + AddScoped(sp => factory.CreateDbContext())).
                // IDbContextOptionsConfiguration<AppDbContext> must also be removed —
                // AddDbContextFactory registers it additively (AddSingleton, not
                // TryAdd), so without this both the Npgsql and InMemory configurations
                // get applied together and EF refuses to pick a single provider.
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextFactory<AppDbContext>>();
                services.RemoveAll<AppDbContext>();
                services.RemoveAll(typeof(IDbContextOptionsConfiguration<AppDbContext>));

                services.AddDbContextFactory<AppDbContext>(opt => opt.UseInMemoryDatabase(_dbName));
                services.AddScoped<AppDbContext>(sp =>
                    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
            });
        }
    }
}
