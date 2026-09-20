using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;
using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Controllers;

[ApiController]
[Route("api/workout-sessions")]
[Authorize]
public class WorkoutSessionsController(AppDbContext db) : ControllerBase
{
    // Sync batches are normally small (a device's offline queue of newly-finished
    // workouts) — this caps a malformed/runaway request rather than any real usage.
    private const int MaxSyncBatchSize = 500;
    private const int DefaultHistoryPageSize = 200;
    private const int MaxHistoryPageSize = 500;

    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/workout-sessions/sync — batch idempotent upsert, keyed by client-generated Id.
    // Replaying the same batch is a no-op: existing ids are skipped (first-write-wins,
    // sessions are immutable/append-only), and every id now present server-side — whether
    // just inserted or already there — comes back in savedIds. A soft-deleted id is left
    // deleted (never resurrected) — it already counts as "existing" so it's skipped too.
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncWorkoutSessionsRequest req)
    {
        var uid = Me;
        var savedIds = new List<string>();

        if (req.Sessions is { Count: > 0 })
        {
            if (req.Sessions.Count > MaxSyncBatchSize)
                return BadRequest(new { message = $"A sync batch can contain at most {MaxSyncBatchSize} sessions." });

            var incomingIds = req.Sessions.Select(s => s.Id).ToList();
            var existingIds = new HashSet<Guid>(await db.WorkoutSessions
                .Where(s => s.UserId == uid && incomingIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync());

            // Two entries sharing an Id within the SAME batch would otherwise both hit
            // Add() and throw a duplicate-key exception on SaveChanges. Deterministic,
            // documented policy: first occurrence in the batch wins — consistent with
            // the existing across-request rule (sessions are immutable/append-only, so
            // "first write wins" already applies; this just enforces it within a single
            // request too). Every occurrence is still acknowledged in savedIds, since
            // the id IS present server-side by the time the response is built either way.
            var seenThisBatch = new HashSet<Guid>();

            foreach (var s in req.Sessions)
            {
                if (s.Id == Guid.Empty)
                    continue; // malformed — no client id, can't dedupe safely

                if (existingIds.Contains(s.Id) || !seenThisBatch.Add(s.Id))
                {
                    savedIds.Add(s.Id.ToString());
                    continue;
                }

                if (!DateTime.TryParse(s.StartedAt, out var startedAt) ||
                    !DateTime.TryParse(s.EndedAt, out var endedAt))
                    continue; // malformed — not saved, client should retry after fixing

                var templateId = Guid.TryParse(s.TemplateId, out var tid) ? tid : (Guid?)null;
                var now = DateTime.UtcNow;

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
                    CreatedAtServer = now,
                    UpdatedAt = now,
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
    // Fast recent-history path — unchanged contract. Deleted sessions are excluded.
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? sinceDays, [FromQuery] string? since)
    {
        var uid = Me;

        DateTime? cutoff = null;
        if (!string.IsNullOrWhiteSpace(since) && DateTime.TryParse(since, out var sinceDate))
            cutoff = sinceDate.ToUniversalTime();
        else if (sinceDays.HasValue)
            cutoff = DateTime.UtcNow.AddDays(-sinceDays.Value);

        var query = db.WorkoutSessions.AsNoTracking().Where(s => s.UserId == uid && s.DeletedAt == null);
        if (cutoff.HasValue)
            query = query.Where(s => s.EndedAt >= cutoff.Value);

        var sessions = await query.OrderByDescending(s => s.EndedAt).ToListAsync();
        return Ok(sessions.Select(ToDto));
    }

    // GET /api/workout-sessions/history — durable full-history reconciliation: cursor
    // pagination over (UpdatedAt, Id) ascending. Same mechanism serves both initial
    // backfill (cursor=null, page until hasMore=false) and incremental sync (resume
    // from the last nextCursor a device persisted) — a delete bumps UpdatedAt exactly
    // like an insert does, so tombstones surface through this same stream naturally.
    [HttpGet("history")]
    public async Task<IActionResult> History(
        [FromQuery] string? cursor,
        [FromQuery] int limit = DefaultHistoryPageSize,
        [FromQuery] bool includeDeleted = true)
    {
        var uid = Me;
        limit = Math.Clamp(limit, 1, MaxHistoryPageSize);

        IQueryable<WorkoutSession> query = db.WorkoutSessions.AsNoTracking().Where(s => s.UserId == uid);
        if (!includeDeleted)
            query = query.Where(s => s.DeletedAt == null);

        if (SessionCursor.TryDecode(cursor, out var cursorUpdatedAt, out var cursorId))
        {
            query = query.Where(s =>
                s.UpdatedAt > cursorUpdatedAt ||
                (s.UpdatedAt == cursorUpdatedAt && s.Id > cursorId));
        }

        // Fetch one extra row to know whether there's a next page without a second query.
        var page = await query
            .OrderBy(s => s.UpdatedAt).ThenBy(s => s.Id)
            .Take(limit + 1)
            .ToListAsync();

        var hasMore = page.Count > limit;
        var items = hasMore ? page.Take(limit).ToList() : page;
        var nextCursor = hasMore && items.Count > 0
            ? SessionCursor.Encode(items[^1].UpdatedAt, items[^1].Id)
            : null;

        return Ok(new WorkoutSessionHistoryResponse(
            items.Select(ToDto).ToList(), nextCursor, hasMore));
    }

    // DELETE /api/workout-sessions/{id} — idempotent soft-delete: 204 whether it
    // existed, was already deleted, or isn't owned by the caller (mirrors
    // TemplatesController.Delete). Never physically removes the row, so other devices
    // can discover the tombstone via /history and reconcile their local copy.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var uid = Me;
        var session = await db.WorkoutSessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == uid);
        if (session is not null && session.DeletedAt is null)
        {
            session.DeletedAt = DateTime.UtcNow;
            session.UpdatedAt = session.DeletedAt.Value;
            await db.SaveChangesAsync();

            await RecomputeStreakAsync(uid);
            await db.SaveChangesAsync();
        }
        return NoContent();
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
            .AsNoTracking()
            .Where(s => s.TemplateId == templateId && allowedUserIds.Contains(s.UserId) && s.DeletedAt == null)
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
            .AsNoTracking()
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

    // Recomputed from scratch from every qualifying workout on every sync — never
    // incremented per-push — so replaying a batch can never double-count a day.
    // Day-grouping and the 48h continuation check both live in StreakCalculator
    // (see there for the exact rule and why it's a pure, separately-tested function).
    private async Task RecomputeStreakAsync(Guid userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return;

        var endedAts = await db.WorkoutSessions
            .Where(s => s.UserId == userId && s.DeletedAt == null)
            .Select(s => s.EndedAt)
            .ToListAsync();

        var timeZone = StreakCalculator.ResolveTimeZone(user.TimeZoneId);
        var result = StreakCalculator.Compute(endedAts, timeZone, DateTime.UtcNow);

        user.CurrentStreak = result.CurrentStreak;
        user.LastQualifyingWorkoutAt = result.LastQualifyingWorkoutAtUtc;
        user.LastWorkoutDate = result.LastWorkoutLocalDate is { } d
            ? DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
            : null;

        user.BestStreak = StreakCalculator.UpdateBestStreak(user.CurrentStreak, user.BestStreak);
    }

    internal static WorkoutSessionDto ToDto(WorkoutSession s) => new(
        s.Id.ToString(),
        s.TemplateId?.ToString(),
        s.TemplateName,
        s.TemplateIcon,
        s.StartedAt.ToString("o"),
        s.EndedAt.ToString("o"),
        s.DurationMs,
        s.LogsJson.RootElement.Clone(),
        s.DeletedAt?.ToString("o"));
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

// DeletedAt is null for every active session — omitted from JSON entirely (global
// WhenWritingNull policy), so existing clients that don't know about deletion never
// see this field change shape for anything they already handle.
public record WorkoutSessionDto(
    string Id,
    string? TemplateId,
    string? TemplateName,
    string? TemplateIcon,
    string StartedAt,
    string EndedAt,
    long DurationMs,
    JsonElement Logs,
    string? DeletedAt = null);

public record WorkoutSessionHistoryResponse(
    List<WorkoutSessionDto> Sessions,
    string? NextCursor,
    bool HasMore);

public record FriendsRankingDto(
    string UserId,
    string Username,
    int Rank,
    double TotalVolume,
    string SessionId,
    string CompletedAt);
