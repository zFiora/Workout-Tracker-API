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

    // POST /api/friends/request
    [HttpPost("request")]
    public async Task<IActionResult> SendRequest([FromBody] FriendRequestBody req)
    {
        if (req.AddresseeId == Me)
            return BadRequest(new { message = "Cannot friend yourself." });

        var alreadyExists = await db.Friendships.AnyAsync(f =>
            (f.RequesterId == Me && f.AddresseeId == req.AddresseeId) ||
            (f.RequesterId == req.AddresseeId && f.AddresseeId == Me));

        if (alreadyExists)
            return Conflict(new { message = "Friend request already exists." });

        var friendship = new Friendship
        {
            RequesterId = Me,
            AddresseeId = req.AddresseeId,
            Status      = FriendshipStatus.Pending,
        };
        db.Friendships.Add(friendship);
        await db.SaveChangesAsync();
        return Created($"/api/friends/{friendship.Id}", new { id = friendship.Id });
    }

    // PATCH /api/friends/{id}/respond
    [HttpPatch("{id:guid}/respond")]
    public async Task<IActionResult> Respond(Guid id, [FromBody] RespondBody req)
    {
        var friendship = await db.Friendships
            .FirstOrDefaultAsync(f => f.Id == id && f.AddresseeId == Me);
        if (friendship is null) return NotFound();

        friendship.Status = req.Accept ? FriendshipStatus.Accepted : FriendshipStatus.Declined;
        await db.SaveChangesAsync();
        return Ok(new { status = friendship.Status.ToString().ToLower() });
    }

    // GET /api/friends
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var friends = await db.Friendships
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .Where(f =>
                (f.RequesterId == Me || f.AddresseeId == Me) &&
                f.Status == FriendshipStatus.Accepted)
            .ToListAsync();

        return Ok(friends.Select(f =>
        {
            var other = f.RequesterId == Me ? f.Addressee : f.Requester;
            return UserDto.From(other);
        }));
    }

    // GET /api/friends/pending
    [HttpGet("pending")]
    public async Task<IActionResult> Pending()
    {
        var pending = await db.Friendships
            .Include(f => f.Requester)
            .Where(f => f.AddresseeId == Me && f.Status == FriendshipStatus.Pending)
            .ToListAsync();

        return Ok(pending.Select(f => new
        {
            friendshipId = f.Id,
            requester    = UserDto.From(f.Requester),
            createdAt    = f.CreatedAt,
        }));
    }

    // DELETE /api/friends/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        var f = await db.Friendships.FirstOrDefaultAsync(f =>
            f.Id == id && (f.RequesterId == Me || f.AddresseeId == Me));
        if (f is null) return NotFound();
        db.Friendships.Remove(f);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/friends/leaderboard
    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard()
    {
        var friendIds = await db.Friendships
            .Where(f =>
                (f.RequesterId == Me || f.AddresseeId == Me) &&
                f.Status == FriendshipStatus.Accepted)
            .Select(f => f.RequesterId == Me ? f.AddresseeId : f.RequesterId)
            .ToListAsync();

        friendIds.Add(Me);

        var users = await db.Users
            .Where(u => friendIds.Contains(u.Id))
            .OrderByDescending(u => u.CurrentStreak)
            .Take(50)
            .ToListAsync();

        return Ok(users.Select(UserDto.From));
    }

    // GET /api/friends/search?q=john
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest(new { message = "Query too short." });

        var results = await db.Users
            .Where(u => u.Id != Me &&
                (EF.Functions.ILike(u.Username, $"%{q}%") ||
                 EF.Functions.ILike(u.DisplayName ?? "", $"%{q}%")))
            .Take(20)
            .ToListAsync();

        return Ok(results.Select(UserDto.From));
    }
}

public record FriendRequestBody(Guid AddresseeId);
public record RespondBody(bool Accept);