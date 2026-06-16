namespace WorkoutTrackerAPI.Models;

public class PrEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public int ExerciseId { get; set; }
    public DateTime PerformedAt { get; set; }
    public double WeightKg { get; set; }
    public int Reps { get; set; }
    public required string Kind { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
