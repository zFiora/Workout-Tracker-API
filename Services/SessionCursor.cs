using System.Text;

namespace WorkoutTrackerAPI.Services;

// Opaque cursor for /api/workout-sessions/history pagination, encoding a position in
// the (UpdatedAt, Id) ordering. Clients round-trip this string verbatim — they never
// construct or parse it — so the internal encoding can change later without breaking
// anyone.
public static class SessionCursor
{
    public static string Encode(DateTime updatedAtUtc, Guid id)
    {
        var raw = $"{updatedAtUtc.Ticks}|{id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string? cursor, out DateTime updatedAtUtc, out Guid id)
    {
        updatedAtUtc = default;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor))
            return false;

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|', 2);
            if (parts.Length != 2)
                return false;
            if (!long.TryParse(parts[0], out var ticks))
                return false;
            if (!Guid.TryParse(parts[1], out var parsedId))
                return false;

            updatedAtUtc = new DateTime(ticks, DateTimeKind.Utc);
            id = parsedId;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
