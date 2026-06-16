using System.Text.Json;

namespace WorkoutTrackerAPI.Models;

public class WorkoutEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string TemplateId { get; set; }
    public required string TemplateName { get; set; }
    public required string TemplateIcon { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public long DurationMs { get; set; }
    public JsonDocument Logs { get; set; } = JsonDocument.Parse("[]");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
