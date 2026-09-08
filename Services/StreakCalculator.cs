namespace WorkoutTrackerAPI.Services;

// Pure functions, no EF/DbContext dependency — deliberately, so streak math is
// directly unit-testable with fake timestamps instead of requiring a live database.
public static class StreakCalculator
{
    public record Result(int CurrentStreak, DateTime? LastQualifyingWorkoutAtUtc, DateOnly? LastWorkoutLocalDate);

    // Groups qualifying workouts by the user's LOCAL calendar day (rule: same day = one
    // increment), anchoring each day on its LATEST timestamp, then walks the anchors
    // using actual elapsed hours (not calendar-date adjacency) — continuing the streak
    // only while consecutive anchors are less than 48h apart. Exactly 48h breaks it.
    public static Result Compute(IReadOnlyCollection<DateTime> qualifyingEndedAtsUtc, TimeZoneInfo timeZone, DateTime nowUtc)
    {
        if (qualifyingEndedAtsUtc.Count == 0)
            return new Result(0, null, null);

        var dayAnchors = qualifyingEndedAtsUtc
            .Select(AsUtc)
            .GroupBy(t => TimeZoneInfo.ConvertTimeFromUtc(t, timeZone).Date)
            .Select(g => g.Max())
            .OrderByDescending(t => t)
            .ToList();

        var mostRecentAnchor = dayAnchors[0];
        var current = (nowUtc - mostRecentAnchor).TotalHours < 48 ? 1 : 0;

        if (current > 0)
        {
            for (var i = 0; i < dayAnchors.Count - 1; i++)
            {
                if ((dayAnchors[i] - dayAnchors[i + 1]).TotalHours < 48)
                    current++;
                else
                    break;
            }
        }

        var lastLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(mostRecentAnchor, timeZone));
        return new Result(current, mostRecentAnchor, lastLocalDate);
    }

    // Cheap freshness check for reads: no session-table query, just arithmetic against
    // the anchor already persisted on the User row. A stale streak (>=48h since the
    // last qualifying workout, no new sync yet) reports 0 without needing a write.
    public static int EffectiveCurrentStreak(int persistedCurrentStreak, DateTime? lastQualifyingWorkoutAtUtc, DateTime nowUtc) =>
        lastQualifyingWorkoutAtUtc is not null && (nowUtc - lastQualifyingWorkoutAtUtc.Value).TotalHours < 48
            ? persistedCurrentStreak
            : 0;

    // Best streak is a lifetime high-water mark — only ever grows, only ever compared
    // against a freshly recomputed CurrentStreak at sync time (never against a
    // possibly-stale read-time value).
    public static int UpdateBestStreak(int currentStreak, int existingBest) =>
        Math.Max(currentStreak, existingBest);

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTime AsUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
}
