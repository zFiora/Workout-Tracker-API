namespace WorkoutTrackerAPI.Models;

public class MacroProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public bool IsMale { get; set; }
    public int Age { get; set; }
    public double ActivityFactor { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
