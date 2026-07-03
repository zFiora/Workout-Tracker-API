using System.ComponentModel.DataAnnotations;

namespace WorkoutTrackerAPI.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(256)] public required string Email { get; set; }
    [MaxLength(100)] public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    [MaxLength(100)] public string? DisplayName { get; set; }
    public string? AvatarBase64 { get; set; }
    public string? AvatarContentType { get; set; }
    public int CurrentStreak { get; set; }
    public int BestStreak { get; set; }
    public DateTime? LastWorkoutDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<WorkoutSession> WorkoutSessions { get; set; } = [];
    public ICollection<Template> Templates { get; set; } = [];
    public ICollection<PrEvent> PrEvents { get; set; } = [];
    public ICollection<Friendship> SentRequests { get; set; } = [];
    public ICollection<Friendship> ReceivedRequests { get; set; } = [];
    public ICollection<Measurement> Measurements { get; set; } = [];
    public MacroProfile? MacroProfile { get; set; }
}