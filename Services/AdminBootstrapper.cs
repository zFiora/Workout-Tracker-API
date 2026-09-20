using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Services;

// Runs once at startup, right after migration. The idempotency check is "does any
// User with Role == Admin already exist" — not "was this bootstrap email already
// processed" — so once any admin exists (via this bootstrap, a future promotion
// path, or a direct DB edit) this is permanently inert, even if the Admin:Bootstrap*
// config is accidentally left set on a later deploy. There is no public "make me
// admin" endpoint anywhere; this config-driven path is the only way in.
public static class AdminBootstrapper
{
    public static async Task RunAsync(AppDbContext db, IConfiguration config)
    {
        if (await db.Users.AnyAsync(u => u.Role == UserRole.Admin))
            return;

        var email = config["Admin:BootstrapEmail"];
        var username = config["Admin:BootstrapUsername"];
        var password = config["Admin:BootstrapPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        var normalizedEmail = email.ToLower().Trim();
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (existing is not null)
        {
            existing.Role = UserRole.Admin;
        }
        else
        {
            db.Users.Add(new User
            {
                Email = normalizedEmail,
                Username = username?.Trim() ?? normalizedEmail.Split('@')[0],
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                Role = UserRole.Admin,
            });
        }

        await db.SaveChangesAsync();
    }
}
