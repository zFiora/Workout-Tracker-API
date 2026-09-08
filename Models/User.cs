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
    // Precise UTC instant the current streak is anchored to (the latest qualifying
    // workout of the most recent streak day) — lets reads cheaply tell whether the
    // persisted CurrentStreak is still alive (< 48h old) without re-querying sessions.
    public DateTime? LastQualifyingWorkoutAt { get; set; }
    // IANA id (e.g. "America/Los_Angeles"). Null until the client sends one; streak
    // day-boundaries fall back to UTC until then — see StreakCalculator.ResolveTimeZone.
    [MaxLength(64)] public string? TimeZoneId { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<WorkoutSession> WorkoutSessions { get; set; } = [];
    public ICollection<Template> Templates { get; set; } = [];
    public ICollection<PrEvent> PrEvents { get; set; } = [];
    public ICollection<Friendship> SentRequests { get; set; } = [];
    public ICollection<Friendship> ReceivedRequests { get; set; } = [];
    public ICollection<Measurement> Measurements { get; set; } = [];
    public MacroProfile? MacroProfile { get; set; }
}