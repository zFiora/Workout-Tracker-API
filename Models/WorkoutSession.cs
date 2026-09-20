using System.Text.Json;

namespace WorkoutTrackerAPI.Models;

public class WorkoutSession
{
    // Client-generated — this is the entire idempotency mechanism for sync.
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    // Soft reference only: no FK, no cascade. Ad-hoc workouts have none, and
    // deleting a template must never delete or break past session history.
    public Guid? TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public string? TemplateIcon { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public long DurationMs { get; set; }
    public JsonDocument LogsJson { get; set; } = JsonDocument.Parse("[]");

    // When did the workout happen (EndedAt) vs. when did the server first see the row
    // (CreatedAtServer, immutable) vs. when did this row last change from a sync
    // bookkeeping perspective (UpdatedAt — bumped on soft-delete too). These are three
    // different axes; cursor pagination/tombstone discovery keys off UpdatedAt, the
    // recent-history list and streak calc key off EndedAt. Do not conflate them.
    public DateTime CreatedAtServer { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Soft-delete tombstone. Null = active. Row is never physically removed, so a
    // delete on one device can be discovered and reconciled by every other device.
    public DateTime? DeletedAt { get; set; }

    public User User { get; set; } = null!;
}
