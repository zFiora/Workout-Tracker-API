using System.Security.Claims;
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
public class AuthController(AppDbContext db, JwtService jwt, PasswordResetService passwordReset) : ControllerBase
{
    private const string ForgotPasswordGenericMessage = "If that email is registered, we've sent a reset link.";

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
    // registered via status code, body, or timing.
    [HttpPost("forgot-password")]
    [EnableRateLimiting("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !IsValidEmail(req.Email))
            return BadRequest(new { message = "A valid email address is required." });

        await passwordReset.RequestResetAsync(req.Email);
        return Ok(new { message = ForgotPasswordGenericMessage });
    }

    // POST /api/auth/reset-password
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
    {
        var (success, error) = await passwordReset.ResetPasswordAsync(req.Token, req.NewPassword);
        if (!success)
            return BadRequest(new { message = error });

        return Ok(new { message = "Password reset." });
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            _ = new System.Net.Mail.MailAddress(email);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

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