namespace WorkoutTrackerAPI.Models;

public class SavedTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid SharedTemplateId { get; set; }
    public Guid SourceTemplateId { get; set; }
    public required string Name { get; set; }
    public required string IconPath { get; set; }
    public List<int> ExerciseIds { get; set; } = [];
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    public User User { get; set; } = null!;
    public SharedTemplate SharedTemplate { get; set; } = null!;
}
