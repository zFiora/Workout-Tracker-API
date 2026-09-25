using System.ComponentModel.DataAnnotations;

namespace WorkoutTrackerAPI.Models;

public enum BugReportStatus { Open, InProgress, Resolved, Closed }
public enum BugReportPriority { Low, Medium, High, Critical }

public class BugReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    [MaxLength(200)] public required string Title { get; set; }
    public required string Description { get; set; }
    public BugReportStatus Status { get; set; } = BugReportStatus.Open;
    public BugReportPriority Priority { get; set; } = BugReportPriority.Medium;
    [MaxLength(50)] public string? AppVersion { get; set; }
    [MaxLength(30)] public string? Platform { get; set; }
    // Free-text client-supplied summary (e.g. "Pixel 9, Android 15") — never a
    // device identifier/serial.
    [MaxLength(200)] public string? DeviceInfo { get; set; }
    [MaxLength(200)] public string? AppScreen { get; set; }
    public string? ErrorDetails { get; set; }
    public string? ScreenshotBase64 { get; set; }
    public string? ScreenshotContentType { get; set; }
    // Internal only — must never appear in a user-facing DTO.
    public string? AdminNotes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
