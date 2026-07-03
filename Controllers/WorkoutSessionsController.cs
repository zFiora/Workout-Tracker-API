using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/workout-sessions")]
[Authorize]
public class WorkoutSessionsController(AppDbContext db) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/workout-sessions/sync — batch idempotent upsert, keyed by client-generated Id.
    // Replaying the same batch is a no-op: existing ids are skipped (first-write-wins,
    // sessions are immutable/append-only), and every id now present server-side — whether
    // just inserted or already there — comes back in savedIds.
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncWorkoutSessionsRequest req)
    {
        var uid = Me;
        var savedIds = new List<string>();

        if (req.Sessions is { Count: > 0 })
        {
            var incomingIds = req.Sessions.Select(s => s.Id).ToList();
            var existingIds = new HashSet<Guid>(await db.WorkoutSessions
                .Where(s => s.UserId == uid && incomingIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync());

            foreach (var s in req.Sessions)
            {
                if (s.Id == Guid.Empty)
                    continue; // malformed — no client id, can't dedupe safely

                if (existingIds.Contains(s.Id))
                {
                    savedIds.Add(s.Id.ToString());
                    continue;
                }

                if (!DateTime.TryParse(s.StartedAt, out var startedAt) ||
                    !DateTime.TryParse(s.EndedAt, out var endedAt))
                    continue; // malformed — not saved, client should retry after fixing

                var templateId = Guid.TryParse(s.TemplateId, out var tid) ? tid : (Guid?)null;

                db.WorkoutSessions.Add(new WorkoutSession
                {
                    Id = s.Id,
                    UserId = uid,
                    TemplateId = templateId,
                    TemplateName = s.TemplateName,
                    TemplateIcon = s.TemplateIcon,
                    StartedAt = startedAt.ToUniversalTime(),
                    EndedAt = endedAt.ToUniversalTime(),
                    DurationMs = s.DurationMs,
                    LogsJson = JsonDocument.Parse(JsonSerializer.Serialize(s.Logs)),
                });
                savedIds.Add(s.Id.ToString());
            }

            await db.SaveChangesAsync();
            await RecomputeStreakAsync(uid);
            await db.SaveChangesAsync();
        }

        return Ok(new SyncWorkoutSessionsResponse(savedIds, DateTime.UtcNow.ToString("o")));
    }

    // GET /api/workout-sessions?sinceDays=7 (or ?since=2026-06-01T00:00:00Z)
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? sinceDays, [FromQuery] string? since)
    {
        var uid = Me;

        DateTime? cutoff = null;
        if (!string.IsNullOrWhiteSpace(since) && DateTime.TryParse(since, out var sinceDate))
            cutoff = sinceDate.ToUniversalTime();
        else if (sinceDays.HasValue)
            cutoff = DateTime.UtcNow.AddDays(-sinceDays.Value);

        var query = db.WorkoutSessions.Where(s => s.UserId == uid);
        if (cutoff.HasValue)
            query = query.Where(s => s.EndedAt >= cutoff.Value);

        var sessions = await query.OrderByDescending(s => s.EndedAt).ToListAsync();
        return Ok(sessions.Select(ToDto));
    }

    // GET /api/workout-sessions/{templateId}/friends-ranking
    [HttpGet("{templateId:guid}/friends-ranking")]
    public async Task<IActionResult> FriendsRanking(Guid templateId)
    {
        var uid = Me;

        var templateExists = await db.Templates.AnyAsync(t => t.Id == templateId && t.DeletedAt == null);
        if (!templateExists) return NotFound(new { message = "Template not found." });

        var friendIds = await db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == uid || f.AddresseeId == uid))
            .Select(f => f.RequesterId == uid ? f.AddresseeId : f.RequesterId)
            .ToListAsync();

        var allowedUserIds = new HashSet<Guid>(friendIds) { uid };

        var sessions = await db.WorkoutSessions
            .Where(s => s.TemplateId == templateId && allowedUserIds.Contains(s.UserId))
            .ToListAsync();

        var scored = sessions
            .Select(s => new { Session = s, TotalVolume = ComputeTotalVolume(s.LogsJson) })
            .ToList();

        var bestPerUser = scored
            .GroupBy(x => x.Session.UserId)
            .Select(g => g.OrderByDescending(x => x.TotalVolume).First())
            .OrderByDescending(x => x.TotalVolume)
            .ToList();

        if (bestPerUser.Count == 0)
            return Ok(Array.Empty<FriendsRankingDto>());

        var userIds = bestPerUser.Select(x => x.Session.UserId).ToList();
        var users = await db.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);

        var ranking = bestPerUser.Select((x, idx) => new FriendsRankingDto(
            x.Session.UserId.ToString(),
            users[x.Session.UserId].Username,
            idx + 1,
            x.TotalVolume,
            x.Session.Id.ToString(),
            x.Session.EndedAt.ToString("o")));

        return Ok(ranking);
    }

    private static double ComputeTotalVolume(JsonDocument logsJson)
    {
        double total = 0;
        foreach (var log in logsJson.RootElement.EnumerateArray())
        {
            if (!log.TryGetProperty("sets", out var sets)) continue;
            foreach (var set in sets.EnumerateArray())
            {
                var weight = set.TryGetProperty("weight", out var w) ? w.GetDouble() : 0;
                var reps = set.TryGetProperty("reps", out var r) ? r.GetInt32() : 0;
                total += weight * reps;
            }
        }
        return total;
    }

    // Recomputed from scratch from the distinct set of workout days on every sync —
    // never incremented per-push — so replaying a batch can never double-count a day.
    // A day counts once no matter how many sessions land on it; a gap of 2+ days since
    // the most recent workout day (relative to now) breaks the streak back to 0.
    private async Task RecomputeStreakAsync(Guid userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return;

        var workoutDates = await db.WorkoutSessions
            .Where(s => s.UserId == userId)
            .Select(s => s.EndedAt.Date)
            .Distinct()
            .OrderByDescending(d => d)
            .ToListAsync();

        if (workoutDates.Count == 0)
        {
            user.CurrentStreak = 0;
            user.LastWorkoutDate = null;
            return;
        }

        var mostRecent = workoutDates[0];
        var today = DateTime.UtcNow.Date;

        var current = mostRecent < today.AddDays(-1) ? 0 : 1;
        if (current > 0)
        {
            for (var i = 0; i < workoutDates.Count - 1; i++)
            {
                if (workoutDates[i].AddDays(-1) == workoutDates[i + 1])
                    current++;
                else
                    break;
            }
        }

        user.CurrentStreak = current;
        user.LastWorkoutDate = mostRecent;

        if (user.CurrentStreak > user.BestStreak)
            user.BestStreak = user.CurrentStreak;
    }

    internal static WorkoutSessionDto ToDto(WorkoutSession s) => new(
        s.Id.ToString(),
        s.TemplateId?.ToString(),
        s.TemplateName,
        s.TemplateIcon,
        s.StartedAt.ToString("o"),
        s.EndedAt.ToString("o"),
        s.DurationMs,
        s.LogsJson.RootElement.Clone());
}

public record SyncSessionRequest(
    Guid Id,
    string? TemplateId,
    string? TemplateName,
    string? TemplateIcon,
    string StartedAt,
    string EndedAt,
    long DurationMs,
    List<JsonElement> Logs);

public record SyncWorkoutSessionsRequest(List<SyncSessionRequest> Sessions);

public record SyncWorkoutSessionsResponse(List<string> SavedIds, string ServerTime);

public record WorkoutSessionDto(
    string Id,
    string? TemplateId,
    string? TemplateName,
    string? TemplateIcon,
    string StartedAt,
    string EndedAt,
    long DurationMs,
    JsonElement Logs);

public record FriendsRankingDto(
    string UserId,
    string Username,
    int Rank,
    double TotalVolume,
    string SessionId,
    string CompletedAt);
