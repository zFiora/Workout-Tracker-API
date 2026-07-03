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
    public DateTime CreatedAtServer { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
