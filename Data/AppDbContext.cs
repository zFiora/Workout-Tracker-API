using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<PrEvent> PrEvents => Set<PrEvent>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<Measurement> Measurements => Set<Measurement>();
    public DbSet<MacroProfile> MacroProfiles => Set<MacroProfile>();
    public DbSet<SharedTemplate> SharedTemplates => Set<SharedTemplate>();
    public DbSet<SavedTemplate> SavedTemplates => Set<SavedTemplate>();
    public DbSet<WorkoutSession> WorkoutSessions => Set<WorkoutSession>();
    public DbSet<ExerciseNote> ExerciseNotes => Set<ExerciseNote>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<BugReport> BugReports => Set<BugReport>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>()
            .HasIndex(u => u.Email).IsUnique();
        b.Entity<User>()
            .HasIndex(u => u.Username).IsUnique();
        b.Entity<User>()
            .Property(u => u.Role)
            .HasConversion<string>()
            .HasMaxLength(20);
        b.Entity<User>()
            .HasIndex(u => u.Role);

        b.Entity<Template>()
            .Property(t => t.ExerciseIds)
            .HasColumnType("integer[]");

        b.Entity<Friendship>()
            .Property(f => f.Status)
            .HasConversion<string>();
        b.Entity<Friendship>()
            .HasIndex(f => new { f.RequesterId, f.AddresseeId })
            .IsUnique();
        b.Entity<Friendship>()
            .HasOne(f => f.Requester)
            .WithMany(u => u.SentRequests)
            .HasForeignKey(f => f.RequesterId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<Friendship>()
            .HasOne(f => f.Addressee)
            .WithMany(u => u.ReceivedRequests)
            .HasForeignKey(f => f.AddresseeId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<MacroProfile>()
            .HasIndex(m => m.UserId)
            .IsUnique();
        b.Entity<MacroProfile>()
            .Property(m => m.DateOfBirth)
            .HasColumnType("date");

        b.Entity<PasswordResetToken>()
            .HasIndex(t => t.TokenHash)
            .IsUnique();
        b.Entity<PasswordResetToken>()
            .HasIndex(t => new { t.UserId, t.UsedAtUtc });
        b.Entity<PasswordResetToken>()
            .HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<SharedTemplate>()
            .HasOne(s => s.Template)
            .WithMany()
            .HasForeignKey(s => s.TemplateId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<SharedTemplate>()
            .HasOne(s => s.Owner)
            .WithMany()
            .HasForeignKey(s => s.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<SharedTemplate>()
            .HasOne(s => s.SharedWithUser)
            .WithMany()
            .HasForeignKey(s => s.SharedWithUserId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<SharedTemplate>()
            .HasIndex(s => new { s.TemplateId, s.SharedWithUserId })
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        b.Entity<SavedTemplate>()
            .Property(s => s.ExerciseIds)
            .HasColumnType("integer[]");
        b.Entity<SavedTemplate>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<SavedTemplate>()
            .HasOne(s => s.SharedTemplate)
            .WithMany()
            .HasForeignKey(s => s.SharedTemplateId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<SavedTemplate>()
            .HasIndex(s => new { s.SharedTemplateId, s.UserId })
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        b.Entity<WorkoutSession>()
            .HasOne(s => s.User)
            .WithMany(u => u.WorkoutSessions)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        var logsJson = b.Entity<WorkoutSession>().Property(s => s.LogsJson);
        logsJson.HasColumnType("jsonb");
        // Npgsql has native JsonDocument support for jsonb columns and needs no
        // converter. EF Core's InMemory provider (test-only — see
        // WorkoutTrackerAPI.Tests) has no native support for JsonDocument at all and
        // fails model validation without one, so give it a plain string round-trip.
        // Real (Npgsql) behavior is completely unaffected by this branch.
        if (Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
        {
            logsJson.HasConversion(
                v => v.RootElement.GetRawText(),
                v => System.Text.Json.JsonDocument.Parse(v, new System.Text.Json.JsonDocumentOptions()));
        }
        b.Entity<WorkoutSession>()
            .HasIndex(s => new { s.UserId, s.Id })
            .IsUnique();
        b.Entity<WorkoutSession>()
            .HasIndex(s => new { s.UserId, s.TemplateId });
        b.Entity<WorkoutSession>()
            .HasIndex(s => new { s.UserId, s.EndedAt });
        b.Entity<WorkoutSession>()
            .HasIndex(s => new { s.UserId, s.UpdatedAt, s.Id });

        b.Entity<ExerciseNote>()
            .HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<ExerciseNote>()
            .HasIndex(n => new { n.UserId, n.ExerciseId });

        b.Entity<BugReport>()
            .Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(20);
        b.Entity<BugReport>()
            .Property(r => r.Priority)
            .HasConversion<string>()
            .HasMaxLength(20);
        b.Entity<BugReport>()
            .HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<BugReport>()
            .HasIndex(r => r.Status);

        // No HasOne/WithMany here on purpose — see the comment on AdminAuditLog itself.
        b.Entity<AdminAuditLog>()
            .HasIndex(a => a.CreatedAt);
        b.Entity<AdminAuditLog>()
            .HasIndex(a => a.AdminUserId);
        b.Entity<AdminAuditLog>()
            .HasIndex(a => a.Action);
        b.Entity<AdminAuditLog>()
            .HasIndex(a => a.TargetType);
    }
}