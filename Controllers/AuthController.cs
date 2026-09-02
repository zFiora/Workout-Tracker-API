using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;
using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(AppDbContext db, JwtService jwt, IEmailService email, IConfiguration config) : ControllerBase
{
    private const string ForgotPasswordGenericMessage = "If that email is registered, we've sent a reset link.";
    private const int MaxResetRequestsPerWindow = 5;
    private static readonly TimeSpan ResetRequestWindow = TimeSpan.FromMinutes(15);

    // POST /api/auth/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        if (await db.Users.AnyAsync(u => u.Email == req.Email))
            return Conflict(new { message = "Email already in use." });

        if (await db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict(new { message = "Username already taken." });

        var user = new User
        {
            Email        = req.Email.ToLower().Trim(),
            Username     = req.Username.Trim(),
            DisplayName  = req.DisplayName?.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return Ok(new AuthResponse(jwt.Generate(user), ToDto(user)));
    }

    // POST /api/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.Email == req.Identity.ToLower() || u.Username == req.Identity);

        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new { message = "Invalid credentials." });

        return Ok(new AuthResponse(jwt.Generate(user), ToDto(user)));
    }

    // POST /api/auth/refresh
    [HttpPost("refresh")]
    [Authorize]
    public async Task<IActionResult> Refresh()
    {
        var id   = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await db.Users.FindAsync(id);
        if (user is null) return Unauthorized();
        return Ok(new AuthResponse(jwt.Generate(user), ToDto(user)));
    }

    // POST /api/auth/change-password
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var id   = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await db.Users.FindAsync(id);
        if (user is null) return Unauthorized();

        if (!BCrypt.Net.BCrypt.Verify(req.CurrentPassword, user.PasswordHash))
            return Unauthorized(new { message = "Current password is incorrect." });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        await db.SaveChangesAsync();

        return Ok();
    }

    // POST /api/auth/forgot-password
    // Always 200 with an identical generic body — never leaks whether the email is
    // registered via status code, body, or timing (see docs/backend-contracts.md).
    [HttpPost("forgot-password")]
    [EnableRateLimiting("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        var emailLower = req.Email.Trim().ToLower();
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

                var ttlMinutes = int.TryParse(config["PASSWORD_RESET_TOKEN_TTL_MINUTES"], out var ttl) ? ttl : 30;
                var rawToken = GenerateResetToken();

                db.PasswordResetTokens.Add(new PasswordResetToken
                {
                    UserId = user.Id,
                    TokenHash = HashToken(rawToken),
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(ttlMinutes),
                });
                await db.SaveChangesAsync();

                await email.SendPasswordResetEmailAsync(user.Email, rawToken, ttlMinutes);
            }
        }

        // Fixed floor latency so response time doesn't distinguish "user exists" from "doesn't".
        await Task.Delay(150);
        return Ok(new { message = ForgotPasswordGenericMessage });
    }

    // POST /api/auth/reset-password
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest(new { message = "Password must be at least 8 characters." });

        var tokenHash = HashToken(req.Token);
        var entry = await db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

        if (entry is null)
            return BadRequest(new { message = "This reset link is invalid." });

        if (entry.UsedAtUtc is not null)
            return BadRequest(new { message = "This reset link has already been used." });

        if (entry.ExpiresAtUtc < DateTime.UtcNow)
            return BadRequest(new { message = "This reset link has expired. Request a new one." });

        entry.User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        entry.User.PasswordChangedAt = DateTime.UtcNow;
        entry.UsedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync();
        await email.SendPasswordChangedNotificationAsync(entry.User.Email);

        return Ok(new { message = "Password reset." });
    }

    private static string GenerateResetToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static UserDto ToDto(User u) => new(
        u.Id.ToString(), u.Email, u.Username, u.DisplayName,
        u.AvatarBase64, u.AvatarContentType, u.CurrentStreak, u.BestStreak,
        u.LastWorkoutDate?.ToString("yyyy-MM-dd"));
}

public record RegisterRequest(
    string Email, string Username,
    string Password, string? DisplayName);

public record LoginRequest(string Identity, string Password);
public record AuthResponse(string Token, UserDto User);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);