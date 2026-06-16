namespace WorkoutTrackerAPI.Models;

public enum FriendshipStatus { Pending, Accepted, Declined, Blocked }

public class Friendship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequesterId { get; set; }
    public Guid AddresseeId { get; set; }
    public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User Requester { get; set; } = null!;
    public User Addressee { get; set; } = null!;
}