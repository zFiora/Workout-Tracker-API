namespace WorkoutTrackerAPI.Models;

public class SharedTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid SharedWithUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    public Template Template { get; set; } = null!;
    public User Owner { get; set; } = null!;
    public User SharedWithUser { get; set; } = null!;
}
