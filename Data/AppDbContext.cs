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

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>()
            .HasIndex(u => u.Email).IsUnique();
        b.Entity<User>()
            .HasIndex(u => u.Username).IsUnique();

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
        b.Entity<WorkoutSession>()
            .Property(s => s.LogsJson)
            .HasColumnType("jsonb");
        b.Entity<WorkoutSession>()
            .HasIndex(s => new { s.UserId, s.Id })
            .IsUnique();
        b.Entity<WorkoutSession>()
            .HasIndex(s => new { s.UserId, s.TemplateId });
        b.Entity<WorkoutSession>()
            .HasIndex(s => new { s.UserId, s.EndedAt });

        b.Entity<ExerciseNote>()
            .HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<ExerciseNote>()
            .HasIndex(n => new { n.UserId, n.ExerciseId });
    }
}