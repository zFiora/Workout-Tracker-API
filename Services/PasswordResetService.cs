using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Services;

public class PasswordResetService(AppDbContext db, IEmailService email, IConfiguration config)
{
    private const int MaxResetRequestsPerWindow = 5;
    private static readonly TimeSpan ResetRequestWindow = TimeSpan.FromMinutes(15);

    // Always completes the same way regardless of whether the email is registered —
    // callers must return an identical generic response either way.
    public async Task RequestResetAsync(string rawEmail)
    {
        var emailLower = rawEmail.Trim().ToLower();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == emailLower);

        if (user is not null)
        {
            var windowStart = DateTime.UtcNow - ResetRequestWindow;
            var recentCount = await db.PasswordResetTokens
                .CountAsync(t => t.UserId == user.Id && t.CreatedAtUtc >= windowStart);

            if (recentCount < MaxResetRequestsPerWindow)
            {
                var outstanding = await db.PasswordResetTokens
                    .Where(t => t.UserId == user.Id && t.UsedAtUtc == null)
                    .ToListAsync();
                foreach (var old in outstanding)
                    old.UsedAtUtc = DateTime.UtcNow;

                var expirationMinutes = int.TryParse(config["PasswordReset:ExpirationMinutes"], out var m) ? m : 30;
                var scheme = config["PasswordReset:ResetUrlScheme"];
                if (string.IsNullOrWhiteSpace(scheme)) scheme = "workouttracker";

                var rawToken = GenerateResetToken();
                db.PasswordResetTokens.Add(new PasswordResetToken
                {
                    UserId = user.Id,
                    TokenHash = HashToken(rawToken),
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(expirationMinutes),
                });
                await db.SaveChangesAsync();

                var resetUrl = $"{scheme}://reset-password?token={Uri.EscapeDataString(rawToken)}";
                await email.SendPasswordResetEmailAsync(user.Email, resetUrl, rawToken, expirationMinutes);
            }
        }

        // Fixed floor latency so response time doesn't distinguish "user exists" from "doesn't".
        await Task.Delay(150);
    }

    public async Task<(bool Success, string? ErrorMessage)> ResetPasswordAsync(string token, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            return (false, "Password must be at least 8 characters.");

        var tokenHash = HashToken(token);
        var entry = await db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

        if (entry is null)
            return (false, "This reset link is invalid.");

        if (entry.UsedAtUtc is not null)
            return (false, "This reset link has already been used.");

        if (entry.ExpiresAtUtc < DateTime.UtcNow)
            return (false, "This reset link has expired. Request a new one.");

        entry.User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        entry.User.PasswordChangedAt = DateTime.UtcNow;
        entry.UsedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync();
        await email.SendPasswordChangedNotificationAsync(entry.User.Email);

        return (true, null);
    }

    private static string GenerateResetToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
