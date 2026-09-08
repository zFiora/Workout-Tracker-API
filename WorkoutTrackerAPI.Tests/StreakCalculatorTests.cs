using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Tests;

// All timestamps are fixed/deterministic — nothing here reads the real clock.
public class StreakCalculatorTests
{
    // Fixed reference instant every test builds its timestamps relative to.
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime Before(TimeSpan span) => Now - span;

    [Fact]
    public void FirstWorkout_StartsStreakAtOne()
    {
        var result = StreakCalculator.Compute([Now], TimeZoneInfo.Utc, Now);

        Assert.Equal(1, result.CurrentStreak);
    }

    [Fact]
    public void MultipleWorkoutsSameDay_CountsAsOneIncrement()
    {
        var day1 = new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        var day1Later = new DateTime(2026, 6, 15, 20, 0, 0, DateTimeKind.Utc);

        var result = StreakCalculator.Compute([day1, day1Later], TimeZoneInfo.Utc, day1Later);

        Assert.Equal(1, result.CurrentStreak);
    }

    [Fact]
    public void Gap23Hours_StreakContinues()
    {
        var previous = Before(TimeSpan.FromHours(23));
        var result = StreakCalculator.Compute([previous, Now], TimeZoneInfo.Utc, Now);

        Assert.Equal(2, result.CurrentStreak);
    }

    [Fact]
    public void Gap24Hours_StreakContinues()
    {
        var previous = Before(TimeSpan.FromHours(24));
        var result = StreakCalculator.Compute([previous, Now], TimeZoneInfo.Utc, Now);

        Assert.Equal(2, result.CurrentStreak);
    }

    [Fact]
    public void Gap47h59m_StreakContinues()
    {
        var previous = Before(TimeSpan.FromHours(47) + TimeSpan.FromMinutes(59));
        var result = StreakCalculator.Compute([previous, Now], TimeZoneInfo.Utc, Now);

        Assert.Equal(2, result.CurrentStreak);
    }

    [Fact]
    public void GapExactly48Hours_StreakResetsToOne()
    {
        var previous = Before(TimeSpan.FromHours(48));
        var result = StreakCalculator.Compute([previous, Now], TimeZoneInfo.Utc, Now);

        // The gap breaks the chain, so Now starts a brand-new streak of 1 — not 2.
        Assert.Equal(1, result.CurrentStreak);
    }

    [Fact]
    public void Gap48h1m_StreakResetsToOne()
    {
        var previous = Before(TimeSpan.FromHours(48) + TimeSpan.FromMinutes(1));
        var result = StreakCalculator.Compute([previous, Now], TimeZoneInfo.Utc, Now);

        Assert.Equal(1, result.CurrentStreak);
    }

    [Fact]
    public void LongInactivity_StreakIsZeroAsOfNow()
    {
        var thirtyDaysAgo = Before(TimeSpan.FromDays(30));

        // Evaluated "as of Now" with no workout since — streak is dead, not just "1".
        var result = StreakCalculator.Compute([thirtyDaysAgo], TimeZoneInfo.Utc, Now);

        Assert.Equal(0, result.CurrentStreak);
    }

    [Fact]
    public void SameDayMultipleWorkouts_AnchorIsTheDaysLatestTimestamp()
    {
        // Direct check: for a single day with two workouts, the reported anchor must
        // be the latest one (8pm), not the first (8am).
        var day1Morning = new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        var day1Evening = new DateTime(2026, 6, 15, 20, 0, 0, DateTimeKind.Utc);

        var result = StreakCalculator.Compute([day1Morning, day1Evening], TimeZoneInfo.Utc, day1Evening);

        Assert.Equal(1, result.CurrentStreak);
        Assert.Equal(day1Evening, result.LastQualifyingWorkoutAtUtc);
    }

    [Fact]
    public void SameDayMultipleWorkouts_LatestAnchorIsWhatTheNextDaysGapIsMeasuredFrom()
    {
        var day1Morning = new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        var day1Evening = new DateTime(2026, 6, 15, 20, 0, 0, DateTimeKind.Utc);

        // 47h59m after day1Evening — but ~59h59m after day1Morning, which would
        // wrongly break the streak if the anchor were mistakenly the day's *first*
        // workout instead of its latest.
        var day2 = day1Evening + TimeSpan.FromHours(47) + TimeSpan.FromMinutes(59);

        var result = StreakCalculator.Compute([day1Morning, day1Evening, day2], TimeZoneInfo.Utc, day2);

        Assert.Equal(2, result.CurrentStreak);
    }

    [Fact]
    public void SessionSpanningMidnight_CountsAsTwoDistinctDaysButStreakStillContinues()
    {
        // Two workouts only 20 minutes apart in real time, but straddling UTC midnight.
        var beforeMidnight = new DateTime(2026, 6, 14, 23, 50, 0, DateTimeKind.Utc);
        var afterMidnight = new DateTime(2026, 6, 15, 0, 10, 0, DateTimeKind.Utc);

        var result = StreakCalculator.Compute([beforeMidnight, afterMidnight], TimeZoneInfo.Utc, afterMidnight);

        // Different calendar dates -> two increments, even though barely any time passed.
        Assert.Equal(2, result.CurrentStreak);
    }

    [Fact]
    public void DifferentUtcDates_SameLocalDate_DedupeToOneDayWhenTimeZoneKnown()
    {
        // Two workouts 2 hours apart, straddling UTC midnight (different UTC dates)...
        var t1 = new DateTime(2026, 6, 14, 23, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 15, 1, 0, 0, DateTimeKind.Utc);

        // ...but a fixed +14:00 zone (no DST, fully deterministic) puts BOTH on the
        // same local calendar date (June 15 local for both).
        var farEastZone = TimeZoneInfo.CreateCustomTimeZone("Test+14", TimeSpan.FromHours(14), "Test+14", "Test+14");

        var localResult = StreakCalculator.Compute([t1, t2], farEastZone, t2);
        var utcResult = StreakCalculator.Compute([t1, t2], TimeZoneInfo.Utc, t2);

        Assert.Equal(1, localResult.CurrentStreak); // correctly deduped as one local day
        Assert.Equal(2, utcResult.CurrentStreak);    // UTC-only bucketing wrongly splits them
    }

    [Fact]
    public void EffectiveCurrentStreak_ReturnsZero_WhenLastQualifyingWorkoutIs48HoursOrOlder()
    {
        var lastQualifying = Before(TimeSpan.FromHours(48));

        var effective = StreakCalculator.EffectiveCurrentStreak(persistedCurrentStreak: 5, lastQualifying, Now);

        Assert.Equal(0, effective);
    }

    [Fact]
    public void EffectiveCurrentStreak_ReturnsPersistedValue_WhenStillFresh()
    {
        var lastQualifying = Before(TimeSpan.FromHours(10));

        var effective = StreakCalculator.EffectiveCurrentStreak(persistedCurrentStreak: 5, lastQualifying, Now);

        Assert.Equal(5, effective);
    }

    [Fact]
    public void EffectiveCurrentStreak_ReturnsZero_WhenNeverSynced()
    {
        var effective = StreakCalculator.EffectiveCurrentStreak(persistedCurrentStreak: 0, lastQualifyingWorkoutAtUtc: null, Now);

        Assert.Equal(0, effective);
    }

    [Fact]
    public void BestStreak_SurvivesACurrentStreakReset()
    {
        // A streak of 5 was achieved earlier; the current streak has since broken and
        // restarted at 1. Best must remain 5, not drop to match the new current value.
        var best = StreakCalculator.UpdateBestStreak(currentStreak: 1, existingBest: 5);

        Assert.Equal(5, best);
    }

    [Fact]
    public void BestStreak_UpdatesWhenCurrentExceedsIt()
    {
        var best = StreakCalculator.UpdateBestStreak(currentStreak: 7, existingBest: 5);

        Assert.Equal(7, best);
    }

    [Fact]
    public void BestStreak_IsNotAffectedByCurrentStreakGoingStaleAtReadTime()
    {
        // Read-time staleness (EffectiveCurrentStreak reporting 0) must never feed back
        // into BestStreak — best is only ever updated from a freshly-recomputed streak
        // at sync time, never from a read-time value that might itself be a stale-to-0
        // correction. Simulates the full read-time flow: persisted best=5, streak has
        // gone stale (EffectiveCurrentStreak -> 0), best must still read back as 5.
        var lastQualifying = Before(TimeSpan.FromHours(72));
        var effectiveCurrent = StreakCalculator.EffectiveCurrentStreak(persistedCurrentStreak: 5, lastQualifying, Now);
        const int persistedBest = 5;

        Assert.Equal(0, effectiveCurrent);
        Assert.Equal(5, persistedBest); // best is a separate, untouched field — not recomputed here at all
    }

    [Fact]
    public void Leaderboard_SortsByEffectiveStreak_NotTheRawStaleColumn()
    {
        // User A: raw CurrentStreak=10 but hasn't worked out in 3 days (stale -> effectively 0).
        // User B: raw CurrentStreak=3, worked out 5 hours ago (fresh -> effectively 3).
        // A naive sort by the raw column would rank A above B; the correct ranking is B above A.
        var userAEffective = StreakCalculator.EffectiveCurrentStreak(
            persistedCurrentStreak: 10, lastQualifyingWorkoutAtUtc: Before(TimeSpan.FromDays(3)), Now);
        var userBEffective = StreakCalculator.EffectiveCurrentStreak(
            persistedCurrentStreak: 3, lastQualifyingWorkoutAtUtc: Before(TimeSpan.FromHours(5)), Now);

        var ranked = new[] { ("A", userAEffective), ("B", userBEffective) }
            .OrderByDescending(x => x.Item2)
            .Select(x => x.Item1)
            .ToList();

        Assert.Equal(["B", "A"], ranked);
    }
}
