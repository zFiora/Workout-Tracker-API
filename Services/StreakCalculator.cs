namespace WorkoutTrackerAPI.Services;

// Pure functions, no EF/DbContext dependency — deliberately, so streak math is
// directly unit-testable with fake timestamps instead of requiring a live database.
public static class StreakCalculator
{
    // Rule: one full local calendar rest day is tolerated between qualifying workout
    // days. Two workout days are still "connected" when they're the same day (gap 0),
    // consecutive days (gap 1), or one rest day apart (gap 2). A gap of 3+ calendar
    // days means two or more consecutive rest days occurred, which breaks the streak.
    // This is calendar-day arithmetic only — no elapsed-hour math anywhere below.
    private const int MaxAllowedRestDays = 1;
    private const int MaxAllowedGapDays = MaxAllowedRestDays + 1;

    public record Result(int CurrentStreak, DateTime? LastQualifyingWorkoutAtUtc, DateOnly? LastWorkoutLocalDate);

    // Groups qualifying workouts by the user's LOCAL calendar day (same day = one
    // increment), anchoring each day on its LATEST timestamp. Walks the days from most
    // recent backward, continuing the streak while consecutive days are within the
    // allowed gap, and breaking as soon as two or more rest days separate a pair.
    public static Result Compute(IReadOnlyCollection<DateTime> qualifyingEndedAtsUtc, TimeZoneInfo timeZone, DateTime nowUtc)
    {
        if (qualifyingEndedAtsUtc.Count == 0)
            return new Result(0, null, null);

        var dayAnchors = qualifyingEndedAtsUtc
            .Select(AsUtc)
            .GroupBy(t => LocalDate(t, timeZone))
            .Select(g => g.Max())
            .OrderByDescending(t => t)
            .ToList();

        var mostRecentAnchor = dayAnchors[0];
        var mostRecentLocalDate = LocalDate(mostRecentAnchor, timeZone);
        var todayLocalDate = LocalDate(nowUtc, timeZone);

        var current = WithinAllowedGap(todayLocalDate, mostRecentLocalDate) ? 1 : 0;

        if (current > 0)
        {
            for (var i = 0; i < dayAnchors.Count - 1; i++)
            {
                var laterDay = LocalDate(dayAnchors[i], timeZone);
                var earlierDay = LocalDate(dayAnchors[i + 1], timeZone);
                if (WithinAllowedGap(laterDay, earlierDay))
                    current++;
                else
                    break;
            }
        }

        return new Result(current, mostRecentAnchor, DateOnly.FromDateTime(mostRecentLocalDate));
    }

    // Cheap freshness check for reads: no session-table query, just arithmetic against
    // the anchor already persisted on the User row. A stale streak (the allowed rest
    // window has already been exceeded, no new qualifying workout yet) reports 0
    // without needing a write. Needs the user's timezone since "today" and the anchor's
    // day are both local-calendar-day concepts now, not a timezone-independent duration.
    public static int EffectiveCurrentStreak(int persistedCurrentStreak, DateTime? lastQualifyingWorkoutAtUtc, string? timeZoneId, DateTime nowUtc)
    {
        if (lastQualifyingWorkoutAtUtc is null)
            return 0;

        var timeZone = ResolveTimeZone(timeZoneId);
        var lastLocalDate = LocalDate(AsUtc(lastQualifyingWorkoutAtUtc.Value), timeZone);
        var todayLocalDate = LocalDate(nowUtc, timeZone);

        return WithinAllowedGap(todayLocalDate, lastLocalDate) ? persistedCurrentStreak : 0;
    }

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

    private static bool WithinAllowedGap(DateTime laterLocalDate, DateTime earlierLocalDate) =>
        (laterLocalDate - earlierLocalDate).Days <= MaxAllowedGapDays;

    private static DateTime LocalDate(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone).Date;

    private static DateTime AsUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
}
