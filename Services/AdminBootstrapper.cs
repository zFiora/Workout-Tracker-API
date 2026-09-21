using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Services;

// Runs once at startup, right after migration. Normal idempotency check is "does
// any User with Role == Admin already exist" — so once an admin exists (via this
// bootstrap, a future promotion path, or a direct DB edit) this is inert, even if
// Admin:Bootstrap* is accidentally left set on a later deploy. There is no public
// "make me admin" endpoint anywhere; this config-driven path is the only way in.
//
// Admin:ReplaceExisting is a separate, explicit opt-in escape hatch for the one
// scenario the guard above deliberately blocks: replacing an existing admin (e.g.
// one bootstrapped with a since-inaccessible email) with a new one. It requires a
// SECOND, deliberate signal beyond the three bootstrap variables — Bootstrap* alone
// can never trigger it — so a stray leftover env var can't silently re-promote,
// demote, or change anyone's password on a future restart. When set to "true", it
// demotes every current Admin back to User (never deletes, never touches non-admin
// users), then promotes-or-creates Admin:BootstrapEmail same as the normal path —
// except if that email belongs to an existing account, ReplaceExisting ALSO
// overwrites its password with Admin:BootstrapPassword (the normal, non-replace
// promotion path never does this). Meant to be set once, used once, then removed.
public static class AdminBootstrapper
{
    public static async Task RunAsync(AppDbContext db, IConfiguration config)
    {
        var replaceExisting = config.GetValue<bool>("Admin:ReplaceExisting");
        var anyAdminExists = await db.Users.AnyAsync(u => u.Role == UserRole.Admin);

        if (anyAdminExists && !replaceExisting)
            return;

        var email = config["Admin:BootstrapEmail"];
        var username = config["Admin:BootstrapUsername"];
        var password = config["Admin:BootstrapPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        if (replaceExisting)
        {
            var currentAdmins = await db.Users.Where(u => u.Role == UserRole.Admin).ToListAsync();
            foreach (var admin in currentAdmins)
                admin.Role = UserRole.User;
        }

        var normalizedEmail = email.ToLower().Trim();
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (existing is not null)
        {
            existing.Role = UserRole.Admin;
            existing.IsActive = true;

            // Password is left untouched on the normal (non-replace) promotion
            // path — a stray Bootstrap* var alone must never change an existing
            // user's credentials. Under the explicit ReplaceExisting escape hatch,
            // though, overwriting the password IS the deliberately requested
            // action: the operator is choosing to make Admin:BootstrapPassword
            // this account's new login, not just granting it the Admin role.
            if (replaceExisting)
            {
                existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
            }
        }
        else
        {
            db.Users.Add(new User
            {
                Email = normalizedEmail,
                Username = username?.Trim() ?? normalizedEmail.Split('@')[0],
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                Role = UserRole.Admin,
                IsActive = true,
            });
        }

        await db.SaveChangesAsync();
    }
}
