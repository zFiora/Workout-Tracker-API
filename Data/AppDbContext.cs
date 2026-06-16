using Microsoft.EntityFrameworkCore;
using WorkoutTrackerAPI.Models;

namespace WorkoutTrackerAPI.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<WorkoutEntry> WorkoutEntries => Set<WorkoutEntry>();
    public DbSet<PrEvent> PrEvents => Set<PrEvent>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<Measurement> Measurements => Set<Measurement>();
    public DbSet<MacroProfile> MacroProfiles => Set<MacroProfile>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>()
            .HasIndex(u => u.Email).IsUnique();
        b.Entity<User>()
            .HasIndex(u => u.Username).IsUnique();

        b.Entity<Template>()
            .Property(t => t.ExerciseIds)
            .HasColumnType("integer[]");

        b.Entity<WorkoutEntry>()
            .Property(w => w.Logs)
            .HasColumnType("jsonb");

        b.Entity<WorkoutEntry>()
            .HasIndex(w => new { w.UserId, w.TemplateId, w.StartedAt })
            .IsUnique();

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
    }
}