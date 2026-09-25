using System.ComponentModel.DataAnnotations;

namespace WorkoutTrackerAPI.Models;

// Deliberately NOT a foreign key to User. An audit log is a durable historical record —
// it must remain readable even if the acting admin's account is later deleted or
// demoted, and it must never block that deletion (a Restrict FK here could throw when
// an ex-admin runs the existing self-service DELETE /api/users/me). AdminUsername is a
// snapshot captured at write time for exactly this reason.
public class AdminAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AdminUserId { get; set; }
    [MaxLength(100)] public required string AdminUsername { get; set; }
    [MaxLength(100)] public required string Action { get; set; }
    [MaxLength(50)] public required string TargetType { get; set; }
    [MaxLength(100)] public string? TargetId { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    // Small structured JSON blob for extra context (e.g. {"from":"Open","to":"Closed"}).
    // Never passwords/hashes/tokens/secrets — enforced by convention at every call site,
    // since only trusted server-side code ever constructs this value.
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
