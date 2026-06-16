using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;
using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(AppDbContext db, JwtService jwt) : ControllerBase
{
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

        return Ok(new AuthResponse(jwt.Generate(user), UserDto.From(user)));
    }

    // POST /api/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.Email == req.Identity.ToLower() || u.Username == req.Identity);

        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new { message = "Invalid credentials." });

        return Ok(new AuthResponse(jwt.Generate(user), UserDto.From(user)));
    }

    // POST /api/auth/refresh
    [HttpPost("refresh")]
    [Authorize]
    public async Task<IActionResult> Refresh()
    {
        var id   = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await db.Users.FindAsync(id);
        if (user is null) return Unauthorized();
        return Ok(new AuthResponse(jwt.Generate(user), UserDto.From(user)));
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────
public record RegisterRequest(
    string Email, string Username, string Password, string? DisplayName);

public record LoginRequest(string Identity, string Password);

public record AuthResponse(string Token, UserDto User);

public record UserDto(
    Guid Id, string Email, string Username, string? DisplayName,
    string? AvatarUrl, int CurrentStreak, int BestStreak)
{
    public static UserDto From(User u) => new(
        u.Id, u.Email, u.Username, u.DisplayName,
        u.AvatarUrl, u.CurrentStreak, u.BestStreak);
}