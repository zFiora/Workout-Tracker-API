using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Controllers;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Tests;

// DB-backed controller tests using EF Core's InMemory provider (test-only dependency —
// nothing in the shipped API references it). Covers everything that genuinely needs a
// database to verify: deletion, exclusion filters, pagination, sync-batch safety, and
// streak recomputation after deletion. StreakCalculator's own rule logic is already
// covered in StreakCalculatorTests.cs — these tests exercise the controller wiring
// around it (RecomputeStreakAsync being triggered correctly on delete), not the rule
// itself.
public class WorkoutSessionDeletionAndHistoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly WorkoutSessionsController _controller;

    public WorkoutSessionDeletionAndHistoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _db.Users.Add(new User { Id = _userId, Email = "t@example.com", Username = "t", PasswordHash = "x" });
        _db.SaveChanges();

        _controller = new WorkoutSessionsController(_db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = MakePrincipal(_userId) },
            },
        };
    }

    public void Dispose() => _db.Dispose();

    private static ClaimsPrincipal MakePrincipal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private WorkoutSession AddSessionDirect(
        Guid? id = null, DateTime? endedAt = null, DateTime? updatedAt = null,
        DateTime? deletedAt = null, Guid? userId = null)
    {
        var now = DateTime.UtcNow;
        var session = new WorkoutSession
        {
            Id = id ?? Guid.NewGuid(),
            UserId = userId ?? _userId,
            StartedAt = endedAt ?? now,
            EndedAt = endedAt ?? now,
            DurationMs = 1000,
            LogsJson = JsonDocument.Parse("[]"),
            CreatedAtServer = updatedAt ?? now,
            UpdatedAt = updatedAt ?? now,
            DeletedAt = deletedAt,
        };
        _db.WorkoutSessions.Add(session);
        _db.SaveChanges();
        return session;
    }

    private Task<IActionResult> SyncSession(Guid id, DateTime endedAt)
    {
        var req = new SyncWorkoutSessionsRequest([
            new SyncSessionRequest(id, null, null, null, endedAt.ToString("o"), endedAt.ToString("o"), 1000, []),
        ]);
        return _controller.Sync(req);
    }

    // ---------- Delete: success, idempotency, ownership ----------

    [Fact]
    public async Task Delete_SoftDeletesAndSetsUpdatedAtToDeletedAt()
    {
        var session = AddSessionDirect();

        var result = await _controller.Delete(session.Id);

        Assert.IsType<NoContentResult>(result);
        var reloaded = await _db.WorkoutSessions.FindAsync(session.Id);
        Assert.NotNull(reloaded!.DeletedAt);
        Assert.Equal(reloaded.DeletedAt, reloaded.UpdatedAt);
    }

    [Fact]
    public async Task Delete_DoesNotPhysicallyRemoveTheRow()
    {
        var session = AddSessionDirect();

        await _controller.Delete(session.Id);

        Assert.Equal(1, await _db.WorkoutSessions.CountAsync(s => s.Id == session.Id));
    }

    [Fact]
    public async Task Delete_IsIdempotent_SecondCallDoesNotChangeDeletedAt()
    {
        var session = AddSessionDirect();
        await _controller.Delete(session.Id);
        var firstDeletedAt = (await _db.WorkoutSessions.FindAsync(session.Id))!.DeletedAt;

        var result = await _controller.Delete(session.Id);

        Assert.IsType<NoContentResult>(result);
        var reloaded = await _db.WorkoutSessions.FindAsync(session.Id);
        Assert.Equal(firstDeletedAt, reloaded!.DeletedAt);
    }

    [Fact]
    public async Task Delete_NonExistentId_ReturnsNoContent()
    {
        var result = await _controller.Delete(Guid.NewGuid());
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Delete_NotOwnedByCaller_LeavesItUntouched()
    {
        var otherUserId = Guid.NewGuid();
        _db.Users.Add(new User { Id = otherUserId, Email = "o@example.com", Username = "o", PasswordHash = "x" });
        _db.SaveChanges();
        var session = AddSessionDirect(userId: otherUserId);

        var result = await _controller.Delete(session.Id);

        Assert.IsType<NoContentResult>(result); // still 204 — doesn't reveal ownership
        var reloaded = await _db.WorkoutSessions.FindAsync(session.Id);
        Assert.Null(reloaded!.DeletedAt);
    }

    // ---------- Exclusion from normal reads ----------

    [Fact]
    public async Task List_ExcludesDeletedSessions()
    {
        var active = AddSessionDirect(endedAt: DateTime.UtcNow);
        var deleted = AddSessionDirect(endedAt: DateTime.UtcNow, deletedAt: DateTime.UtcNow);

        var result = await _controller.List(sinceDays: 30, since: null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dtos = Assert.IsAssignableFrom<IEnumerable<WorkoutSessionDto>>(ok.Value).ToList();
        Assert.Contains(dtos, d => d.Id == active.Id.ToString());
        Assert.DoesNotContain(dtos, d => d.Id == deleted.Id.ToString());
    }

    [Fact]
    public async Task FriendsRanking_ExcludesDeletedSessions()
    {
        var template = new Template { Id = Guid.NewGuid(), UserId = _userId, Name = "T", IconPath = "i.png" };
        _db.Templates.Add(template);
        _db.SaveChanges();

        var logs = "[{\"exerciseId\":1,\"sets\":[{\"weight\":100,\"reps\":5}]}]";
        var active = new WorkoutSession
        {
            Id = Guid.NewGuid(), UserId = _userId, TemplateId = template.Id,
            StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow, DurationMs = 1,
            LogsJson = JsonDocument.Parse(logs), UpdatedAt = DateTime.UtcNow,
        };
        var deleted = new WorkoutSession
        {
            Id = Guid.NewGuid(), UserId = _userId, TemplateId = template.Id,
            StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow, DurationMs = 1,
            LogsJson = JsonDocument.Parse(logs), UpdatedAt = DateTime.UtcNow, DeletedAt = DateTime.UtcNow,
        };
        _db.WorkoutSessions.AddRange(active, deleted);
        _db.SaveChanges();

        var result = await _controller.FriendsRanking(template.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var ranking = Assert.IsAssignableFrom<IEnumerable<FriendsRankingDto>>(ok.Value).ToList();
        // Only the active session should have contributed; the deleted one must not
        // produce a ranking entry pointing at it and must not double the volume.
        var entry = Assert.Single(ranking);
        Assert.Equal(active.Id.ToString(), entry.SessionId);
    }

    // ---------- Sync safety: duplicate ids within a batch, batch size cap, no resurrection ----------

    [Fact]
    public async Task Sync_DuplicateIdWithinSameBatch_DoesNotThrow_FirstOccurrenceWins()
    {
        var id = Guid.NewGuid();
        var first = DateTime.UtcNow.AddHours(-5);
        var second = DateTime.UtcNow;

        var req = new SyncWorkoutSessionsRequest([
            new SyncSessionRequest(id, null, null, null, first.ToString("o"), first.ToString("o"), 1000, []),
            new SyncSessionRequest(id, null, null, null, second.ToString("o"), second.ToString("o"), 2000, []),
        ]);

        var result = await _controller.Sync(req); // must not throw

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SyncWorkoutSessionsResponse>(ok.Value);
        Assert.Equal(2, body.SavedIds.Count(x => x == id.ToString())); // both acknowledged

        var stored = await _db.WorkoutSessions.Where(s => s.Id == id).ToListAsync();
        var only = Assert.Single(stored); // only one row was ever created
        Assert.Equal(1000, only.DurationMs); // the FIRST occurrence's data won
    }

    [Fact]
    public async Task Sync_OversizedBatch_IsRejectedWithoutWritingAnything()
    {
        var sessions = Enumerable.Range(0, 501)
            .Select(_ => new SyncSessionRequest(
                Guid.NewGuid(), null, null, null,
                DateTime.UtcNow.ToString("o"), DateTime.UtcNow.ToString("o"), 1000, []))
            .ToList();

        var result = await _controller.Sync(new SyncWorkoutSessionsRequest(sessions));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _db.WorkoutSessions.ToListAsync());
    }

    [Fact]
    public async Task Sync_BatchAtExactLimit_Succeeds()
    {
        var sessions = Enumerable.Range(0, 500)
            .Select(_ => new SyncSessionRequest(
                Guid.NewGuid(), null, null, null,
                DateTime.UtcNow.ToString("o"), DateTime.UtcNow.ToString("o"), 1000, []))
            .ToList();

        var result = await _controller.Sync(new SyncWorkoutSessionsRequest(sessions));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(500, await _db.WorkoutSessions.CountAsync());
    }

    [Fact]
    public async Task Sync_ReplayingADeletedSessionId_DoesNotResurrectIt()
    {
        var id = Guid.NewGuid();
        await SyncSession(id, DateTime.UtcNow);
        await _controller.Delete(id);

        // The device still has its local (now-stale) copy queued and replays it.
        var req = new SyncWorkoutSessionsRequest([
            new SyncSessionRequest(id, null, null, null,
                DateTime.UtcNow.ToString("o"), DateTime.UtcNow.ToString("o"), 1000, []),
        ]);
        var result = await _controller.Sync(req);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SyncWorkoutSessionsResponse>(ok.Value);
        Assert.Contains(id.ToString(), body.SavedIds); // acknowledged as known...

        var reloaded = await _db.WorkoutSessions.FindAsync(id);
        Assert.NotNull(reloaded!.DeletedAt); // ...but still deleted, never resurrected
    }

    // ---------- /history pagination ----------

    [Fact]
    public async Task History_PaginatesAndCursorContinuesAcrossPages()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var s = AddSessionDirect(updatedAt: DateTime.UtcNow.AddSeconds(i)); // strictly increasing order
            ids.Add(s.Id);
        }

        var collected = new List<string>();
        string? cursor = null;
        var pageCount = 0;
        bool hasMore;
        do
        {
            var result = await _controller.History(cursor, limit: 2, includeDeleted: true);
            var body = Assert.IsType<WorkoutSessionHistoryResponse>(Assert.IsType<OkObjectResult>(result).Value);
            collected.AddRange(body.Sessions.Select(s => s.Id));
            cursor = body.NextCursor;
            hasMore = body.HasMore;
            pageCount++;
            Assert.True(pageCount <= 10, "pagination did not terminate");
        } while (hasMore);

        Assert.Equal(3, pageCount); // 5 items, limit 2 -> pages of 2, 2, 1
        Assert.Null(cursor); // no cursor once hasMore is false
        Assert.Equal(ids.Select(i => i.ToString()).ToHashSet(), collected.ToHashSet());
        Assert.Equal(ids.Count, collected.Count); // no duplicates across pages
    }

    [Fact]
    public async Task History_IncludeDeletedFalse_ExcludesTombstones()
    {
        var active = AddSessionDirect();
        var deleted = AddSessionDirect(deletedAt: DateTime.UtcNow);

        var result = await _controller.History(cursor: null, limit: 100, includeDeleted: false);
        var body = Assert.IsType<WorkoutSessionHistoryResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Contains(body.Sessions, s => s.Id == active.Id.ToString());
        Assert.DoesNotContain(body.Sessions, s => s.Id == deleted.Id.ToString());
    }

    [Fact]
    public async Task History_IncludeDeletedTrue_ReturnsTombstoneWithDeletedAtPopulated()
    {
        var deleted = AddSessionDirect(deletedAt: DateTime.UtcNow);

        var result = await _controller.History(cursor: null, limit: 100, includeDeleted: true);
        var body = Assert.IsType<WorkoutSessionHistoryResponse>(Assert.IsType<OkObjectResult>(result).Value);

        var dto = Assert.Single(body.Sessions, s => s.Id == deleted.Id.ToString());
        Assert.NotNull(dto.DeletedAt);
    }

    [Fact]
    public async Task History_ActiveSession_HasNullDeletedAt()
    {
        var active = AddSessionDirect();

        var result = await _controller.History(cursor: null, limit: 100, includeDeleted: true);
        var body = Assert.IsType<WorkoutSessionHistoryResponse>(Assert.IsType<OkObjectResult>(result).Value);

        var dto = Assert.Single(body.Sessions, s => s.Id == active.Id.ToString());
        Assert.Null(dto.DeletedAt);
    }

    [Fact]
    public async Task History_FullBackfill_ReconstructsCompleteSessionSet()
    {
        var expectedIds = new HashSet<string>();
        for (var i = 0; i < 7; i++)
        {
            var s = AddSessionDirect(endedAt: DateTime.UtcNow.AddDays(-i), updatedAt: DateTime.UtcNow.AddSeconds(i));
            expectedIds.Add(s.Id.ToString());
        }

        var collected = new List<string>();
        string? cursor = null;
        var hasMore = true;
        while (hasMore)
        {
            var result = await _controller.History(cursor, limit: 3, includeDeleted: true);
            var body = Assert.IsType<WorkoutSessionHistoryResponse>(Assert.IsType<OkObjectResult>(result).Value);
            collected.AddRange(body.Sessions.Select(s => s.Id));
            cursor = body.NextCursor;
            hasMore = body.HasMore;
        }

        Assert.Equal(expectedIds, collected.ToHashSet());
        Assert.Equal(expectedIds.Count, collected.Count);
    }

    [Fact]
    public async Task History_EmptyDatabase_ReturnsEmptyPageWithHasMoreFalse()
    {
        var result = await _controller.History(cursor: null, limit: 100, includeDeleted: true);
        var body = Assert.IsType<WorkoutSessionHistoryResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Empty(body.Sessions);
        Assert.False(body.HasMore);
        Assert.Null(body.NextCursor);
    }

    [Fact]
    public async Task History_LimitIsClampedToMaximum()
    {
        for (var i = 0; i < 3; i++)
            AddSessionDirect(updatedAt: DateTime.UtcNow.AddSeconds(i));

        // Requesting an absurdly large limit must not error or misbehave — it's clamped.
        var result = await _controller.History(cursor: null, limit: 100_000, includeDeleted: true);
        var body = Assert.IsType<WorkoutSessionHistoryResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(3, body.Sessions.Count);
        Assert.False(body.HasMore);
    }

    // ---------- Streak recomputation after deletion ----------

    [Fact]
    public async Task Delete_OldUnrelatedSession_DoesNotAffectCurrentStreak()
    {
        var oldId = Guid.NewGuid();
        var recentId = Guid.NewGuid();
        await SyncSession(oldId, DateTime.UtcNow.AddDays(-30));
        await SyncSession(recentId, DateTime.UtcNow);

        var userAfterSync = await _db.Users.FindAsync(_userId);
        Assert.Equal(1, userAfterSync!.CurrentStreak); // only recentId is within the streak window

        await _controller.Delete(oldId);

        var userAfterDelete = await _db.Users.FindAsync(_userId);
        Assert.Equal(1, userAfterDelete!.CurrentStreak); // unaffected by deleting an unrelated old session
    }

    [Fact]
    public async Task Delete_CurrentStreakAnchor_RecomputesToTheCorrectRemainingChain()
    {
        var now = DateTime.UtcNow;
        var mondayId = Guid.NewGuid();
        var wednesdayId = Guid.NewGuid();
        var fridayId = Guid.NewGuid();

        await SyncSession(mondayId, now.AddDays(-4));
        await SyncSession(wednesdayId, now.AddDays(-2));
        await SyncSession(fridayId, now); // anchor — "today"

        var userAfterChain = await _db.Users.FindAsync(_userId);
        Assert.Equal(3, userAfterChain!.CurrentStreak);
        Assert.Equal(3, userAfterChain.BestStreak);

        await _controller.Delete(fridayId);

        // Remaining days (Monday, Wednesday) are still each within the one-rest-day
        // allowance of each other AND of "now" (2-day gaps), so the streak correctly
        // recomputes to 2 — not 0, and not left stuck at the old value of 3.
        var userAfterDelete = await _db.Users.FindAsync(_userId);
        Assert.Equal(2, userAfterDelete!.CurrentStreak);
        Assert.Equal(3, userAfterDelete.BestStreak); // historical best untouched
    }

    [Fact]
    public async Task Delete_OnlyQualifyingSession_ResultsInZeroCurrentStreakButBestSurvives()
    {
        var id = Guid.NewGuid();
        await SyncSession(id, DateTime.UtcNow);

        var userAfterSync = await _db.Users.FindAsync(_userId);
        Assert.Equal(1, userAfterSync!.CurrentStreak);
        Assert.Equal(1, userAfterSync.BestStreak);

        await _controller.Delete(id);

        var userAfterDelete = await _db.Users.FindAsync(_userId);
        Assert.Equal(0, userAfterDelete!.CurrentStreak);
        Assert.Equal(1, userAfterDelete.BestStreak); // best is never reduced by a deletion
        Assert.Null(userAfterDelete.LastQualifyingWorkoutAt);
    }

    [Fact]
    public async Task Delete_DoesNotReduceBestStreak_EvenWhenCurrentDropsWellBelowIt()
    {
        var now = DateTime.UtcNow;
        var ids = new List<Guid>();
        // Build a 4-day chain (best=4), then delete the anchor day.
        foreach (var offsetDays in new[] { -6, -4, -2, 0 })
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await SyncSession(id, now.AddDays(offsetDays));
        }

        var userAfterChain = await _db.Users.FindAsync(_userId);
        Assert.Equal(4, userAfterChain!.BestStreak);

        await _controller.Delete(ids[^1]); // delete the most recent (anchor) day

        var userAfterDelete = await _db.Users.FindAsync(_userId);
        Assert.True(userAfterDelete!.CurrentStreak < 4);
        Assert.Equal(4, userAfterDelete.BestStreak); // best is a monotonic high-water mark
    }
}
