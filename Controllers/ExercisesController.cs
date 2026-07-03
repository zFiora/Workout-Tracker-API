using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/exercises")]
[Authorize]
public class ExercisesController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/exercises/{exerciseId}/friends-ranking
    // Best set per user = the one with the highest estimated 1RM (Epley formula),
    // derived from their synced workout session logs rather than a separate PR feed
    // (a session is the single idempotent source of truth; no extra dedup surface).
    [HttpGet("{exerciseId:int}/friends-ranking")]
    public async Task<IActionResult> FriendsRanking(int exerciseId)
    {
        var uid = Me;

        var friendIds = await db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == uid || f.AddresseeId == uid))
            .Select(f => f.RequesterId == uid ? f.AddresseeId : f.RequesterId)
            .ToListAsync();

        var allowedUserIds = new HashSet<Guid>(friendIds) { uid };

        var sessions = await db.WorkoutSessions
            .Where(s => allowedUserIds.Contains(s.UserId))
            .ToListAsync();

        var bestPerUser = new Dictionary<Guid, (double Weight, int Reps, double OneRepMax, DateTime AchievedAt)>();

        foreach (var session in sessions)
        {
            foreach (var log in session.LogsJson.RootElement.EnumerateArray())
            {
                if (!log.TryGetProperty("exerciseId", out var exIdProp) || exIdProp.GetInt32() != exerciseId)
                    continue;
                if (!log.TryGetProperty("sets", out var sets))
                    continue;

                foreach (var set in sets.EnumerateArray())
                {
                    var weight = set.TryGetProperty("weight", out var w) ? w.GetDouble() : 0;
                    var reps = set.TryGetProperty("reps", out var r) ? r.GetInt32() : 0;
                    if (reps <= 0) continue;

                    var oneRepMax = EstimateOneRepMax(weight, reps);
                    var achievedAt = set.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt)
                        ? dt
                        : session.EndedAt;

                    if (!bestPerUser.TryGetValue(session.UserId, out var best) || oneRepMax > best.OneRepMax)
                        bestPerUser[session.UserId] = (weight, reps, oneRepMax, achievedAt);
                }
            }
        }

        if (bestPerUser.Count == 0)
            return Ok(Array.Empty<ExerciseFriendsRankingDto>());

        var userIds = bestPerUser.Keys.ToList();
        var users = await db.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);

        var ranking = bestPerUser
            .OrderByDescending(kv => kv.Value.OneRepMax)
            .Select(kv =>
            {
                var u = users[kv.Key];
                var best = kv.Value;
                return new ExerciseFriendsRankingDto(
                    kv.Key.ToString(),
                    u.DisplayName,
                    u.Username,
                    u.AvatarBase64,
                    u.AvatarContentType,
                    best.Weight,
                    best.Reps,
                    Math.Round(best.OneRepMax, 1),
                    best.AchievedAt.ToString("o"),
                    kv.Key == uid);
            });

        return Ok(ranking);
    }

    private static double EstimateOneRepMax(double weightKg, int reps) =>
        reps <= 1 ? weightKg : weightKg * (1 + reps / 30.0);

    // GET /api/exercises/{exerciseId}/notes
    [HttpGet("{exerciseId:int}/notes")]
    public async Task<IActionResult> ListNotes(int exerciseId)
    {
        var notes = await db.ExerciseNotes
            .Where(n => n.UserId == Me && n.ExerciseId == exerciseId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();

        return Ok(notes.Select(ToNoteDto));
    }

    // POST /api/exercises/{exerciseId}/notes
    [HttpPost("{exerciseId:int}/notes")]
    public async Task<IActionResult> CreateNote(int exerciseId, [FromBody] CreateExerciseNoteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { message = "Text is required." });

        var note = new ExerciseNote
        {
            UserId = Me,
            ExerciseId = exerciseId,
            Text = req.Text,
        };
        db.ExerciseNotes.Add(note);
        await db.SaveChangesAsync();

        return Created($"/api/exercise-notes/{note.Id}", ToNoteDto(note));
    }

    // DELETE /api/exercise-notes/{id}
    [HttpDelete("/api/exercise-notes/{id:guid}")]
    public async Task<IActionResult> DeleteNote(Guid id)
    {
        var note = await db.ExerciseNotes
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == Me);
        if (note is null) return NotFound();

        db.ExerciseNotes.Remove(note);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static ExerciseNoteDto ToNoteDto(ExerciseNote n) => new(
        n.Id.ToString(), n.ExerciseId, n.Text, n.CreatedAt.ToString("o"));
}

public record ExerciseFriendsRankingDto(
    string UserId,
    string? DisplayName,
    string Username,
    string? AvatarBase64,
    string? AvatarContentType,
    double Weight,
    int Reps,
    double OneRepMax,
    string AchievedAt,
    bool IsMe);

public record CreateExerciseNoteRequest(string Text);

public record ExerciseNoteDto(string Id, int ExerciseId, string Text, string CreatedAt);
