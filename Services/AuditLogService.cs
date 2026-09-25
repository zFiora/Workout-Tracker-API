using System.Security.Claims;
using System.Text.Json;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Services;

// Centralizes audit-log writes so no controller hand-rolls the entity or re-derives the
// actor. Adds to the DbContext's change tracker only — callers still call
// SaveChangesAsync themselves, so the audit row commits atomically with the mutation it
// describes rather than as a separate round-trip.
public class AuditLogService(AppDbContext db)
{
    // The normal path: derives the acting admin from the verified, server-side
    // ClaimsPrincipal (HttpContext.User after JWT validation) — never from anything the
    // request body claims about itself. Every AdminController mutation uses this.
    public void RecordForCurrentAdmin(
        ClaimsPrincipal principal, string action, string targetType, string? targetId,
        string? description = null, object? metadata = null)
    {
        var adminId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var adminUsername = principal.FindFirstValue("username") ?? "unknown";
        Record(adminId, adminUsername, action, targetType, targetId, description, metadata);
    }

    // Lower-level overload for the one call site that runs before a ClaimsPrincipal
    // exists — AuthController.Login records "admin.login" immediately after the
    // password has already been BCrypt-verified against the DB, so adminId/adminUsername
    // here come from that freshly-verified row, not from client input.
    public void Record(
        Guid adminId, string adminUsername, string action, string targetType, string? targetId,
        string? description = null, object? metadata = null)
    {
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            AdminUserId = adminId,
            AdminUsername = adminUsername,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Description = description,
            Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata),
        });
    }
}
