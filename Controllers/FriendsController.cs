using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/friends")]
[Authorize]
public class FriendsController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/friends
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var uid = Me;
        var friends = await db.Friendships
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == uid || f.AddresseeId == uid))
            .ToListAsync();

        return Ok(friends.Select(f =>
        {
            var other = f.RequesterId == uid ? f.Addressee : f.Requester;
            return ToDto(other);
        }));
    }

    // GET /api/friends/pending
    [HttpGet("pending")]
    public async Task<IActionResult> Pending()
    {
        var uid = Me;
        var pending = await db.Friendships
            .Include(f => f.Requester)
            .Where(f => f.AddresseeId == uid && f.Status == FriendshipStatus.Pending)
            .ToListAsync();

        return Ok(pending.Select(f => new
        {
            friendshipId = f.Id.ToString(),
            requester    = ToDto(f.Requester),
            createdAt    = f.CreatedAt.ToString("o"),
        }));
    }

    // GET /api/friends/search?q=
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(Array.Empty<object>());

        var uid   = Me;
        var lower = q.ToLower();

        var connectedIds = await db.Friendships
            .Where(f => f.RequesterId == uid || f.AddresseeId == uid)
            .Select(f => f.RequesterId == uid ? f.AddresseeId : f.RequesterId)
            .ToListAsync();

        var results = await db.Users
            .Where(u => u.Id != uid
                && !connectedIds.Contains(u.Id)
                && (u.Username.ToLower().Contains(lower) ||
                    (u.DisplayName != null &&
                     u.DisplayName.ToLower().Contains(lower))))
            .Take(20)
            .ToListAsync();

        return Ok(results.Select(ToDto));
    }

    // POST /api/friends/request
    [HttpPost("request")]
    public async Task<IActionResult> SendRequest([FromBody] FriendRequestBody req)
    {
        var uid         = Me;
        var addresseeId = Guid.Parse(req.AddresseeId);

        if (uid == addresseeId)
            return BadRequest(new { message = "Cannot add yourself." });

        var exists = await db.Friendships.AnyAsync(f =>
            (f.RequesterId == uid && f.AddresseeId == addresseeId) ||
            (f.RequesterId == addresseeId && f.AddresseeId == uid));

        if (exists)
            return Conflict(new { message = "Request already exists." });

        db.Friendships.Add(new Friendship
        {
            RequesterId = uid,
            AddresseeId = addresseeId,
            Status      = FriendshipStatus.Pending,
        });

        await db.SaveChangesAsync();
        return Ok();
    }

    // PATCH /api/friends/{id}/respond
    [HttpPatch("{id:guid}/respond")]
    public async Task<IActionResult> Respond(Guid id, [FromBody] RespondBody req)
    {
        var friendship = await db.Friendships.FindAsync(id);
        if (friendship is null) return NotFound();
        if (friendship.AddresseeId != Me) return Forbid();

        friendship.Status    = req.Accept ? FriendshipStatus.Accepted : FriendshipStatus.Declined;
        friendship.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return Ok();
    }

    // DELETE /api/friends/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        var uid        = Me;
        var friendship = await db.Friendships.FindAsync(id);
        if (friendship is null) return NotFound();
        if (friendship.RequesterId != uid && friendship.AddresseeId != uid)
            return Forbid();

        db.Friendships.Remove(friendship);
        await db.SaveChangesAsync();
        return Ok();
    }

    // GET /api/friends/leaderboard
    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard()
    {
        var uid = Me;

        var friendIds = await db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == uid || f.AddresseeId == uid))
            .Select(f => f.RequesterId == uid ? f.AddresseeId : f.RequesterId)
            .ToListAsync();

        friendIds.Add(uid);

        var users = await db.Users
            .Where(u => friendIds.Contains(u.Id))
            .OrderByDescending(u => u.CurrentStreak)
            .ToListAsync();

        return Ok(users.Select(ToDto));
    }

    private static FriendUserDto ToDto(User u) => new(
        u.Id.ToString(), u.Email, u.Username,
        u.DisplayName, u.AvatarBase64, u.AvatarContentType,
        u.CurrentStreak, u.BestStreak);
}

public record FriendRequestBody(string AddresseeId);
public record RespondBody(bool Accept);
public record FriendUserDto(
    string Id, string Email, string Username,
    string? DisplayName, string? AvatarBase64, string? AvatarContentType,
    int CurrentStreak, int BestStreak);