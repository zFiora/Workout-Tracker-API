using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Tests;

public class SessionCursorTests
{
    [Fact]
    public void EncodeThenDecode_RoundTrips()
    {
        var updatedAt = new DateTime(2026, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid();

        var encoded = SessionCursor.Encode(updatedAt, id);
        var success = SessionCursor.TryDecode(encoded, out var decodedUpdatedAt, out var decodedId);

        Assert.True(success);
        Assert.Equal(updatedAt, decodedUpdatedAt);
        Assert.Equal(id, decodedId);
    }

    [Fact]
    public void TryDecode_Null_ReturnsFalse() =>
        Assert.False(SessionCursor.TryDecode(null, out _, out _));

    [Fact]
    public void TryDecode_Empty_ReturnsFalse() =>
        Assert.False(SessionCursor.TryDecode("", out _, out _));

    [Fact]
    public void TryDecode_NotBase64_ReturnsFalse() =>
        Assert.False(SessionCursor.TryDecode("not-valid-base64!!!", out _, out _));

    [Fact]
    public void TryDecode_ValidBase64ButWrongShape_ReturnsFalse()
    {
        // Valid base64, decodes to a string with no '|' separator.
        var malformed = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("no-separator-here"));
        Assert.False(SessionCursor.TryDecode(malformed, out _, out _));
    }
}
