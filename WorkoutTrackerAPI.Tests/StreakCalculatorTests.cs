using WorkoutTrackerAPI.Services;

namespace WorkoutTrackerAPI.Tests;

// All timestamps are fixed/deterministic — nothing here reads the real clock.
// Rule under test: one full local calendar rest day is tolerated between qualifying
// workout days. Gap of 0/1/2 calendar days between consecutive qualifying days
// continues the streak; a gap of 3+ (two or more consecutive rest days) breaks it.
// No elapsed-hour math anywhere — deliberately, per the current rule.
public class StreakCalculatorTests
{
    // A run of consecutive calendar days, all at 8am UTC unless a test overrides the
    // time-of-day to specifically prove hours don't matter (e.g. the 49h case).
    private static readonly DateTime Saturday  = new(2026, 6, 13, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sunday    = new(2026, 6, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Monday    = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tuesday   = new(2026, 6, 16, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Wednesday = new(2026, 6, 17, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Thursday  = new(2026, 6, 18, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Friday    = new(2026, 6, 19, 8, 0, 0, DateTimeKind.Utc);

    // #1 First qualifying workout day -> 1
    [Fact]
    public void FirstWorkoutDay_StartsStreakAtOne()
    {
        var result = StreakCalculator.Compute([Monday], TimeZoneInfo.Utc, Monday);

        Assert.Equal(1, result.CurrentStreak);
    }

    // #2 Multiple qualifying workouts on the same local calendar day -> still 1
    [Fact]
    public void TwoWorkoutsSameDay_StillCountsAsOne()
    {
        var morning = Monday;
        var evening = Monday.AddHours(12);

        var result = StreakCalculator.Compute([morning, evening], TimeZoneInfo.Utc, evening);

        Assert.Equal(1, result.CurrentStreak);
    }

    // #3 Consecutive local calendar days -> increments
    [Fact]
    public void ConsecutiveDays_Increments()
    {
        // Saturday workout, Sunday workout -> 2
        var result = StreakCalculator.Compute([Saturday, Sunday], TimeZoneInfo.Utc, Sunday);

        Assert.Equal(2, result.CurrentStreak);
    }

    // #4 Exactly one rest day between workout days -> continues
    [Fact]
    public void ExactlyOneRestDay_Continues()
    {
        // Saturday workout, Monday workout -> 2 (Sunday is one allowed rest day)
        var result = StreakCalculator.Compute([Saturday, Monday], TimeZoneInfo.Utc, Monday);

        Assert.Equal(2, result.CurrentStreak);
    }

    // #5 Two or more consecutive rest days -> resets
    [Fact]
    public void TwoConsecutiveRestDays_Resets()
    {
        // Saturday workout, Tuesday workout -> 1 (Sunday + Monday are two rest days)
        var result = StreakCalculator.Compute([Saturday, Tuesday], TimeZoneInfo.Utc, Tuesday);

        Assert.Equal(1, result.CurrentStreak);
    }

    [Fact]
    public void ThreeOrMoreRestDays_AlsoResets()
    {
        // Saturday workout, Wednesday workout -> 1
        var result = StreakCalculator.Compute([Saturday, Wednesday], TimeZoneInfo.Utc, Wednesday);

        Assert.Equal(1, result.CurrentStreak);
    }

    // #6 Three consecutive workout days -> 3
    [Fact]
    public void ThreeConsecutiveWorkoutDays_StreakOfThree()
    {
        var result = StreakCalculator.Compute([Monday, Tuesday, Wednesday], TimeZoneInfo.Utc, Wednesday);

        Assert.Equal(3, result.CurrentStreak);
    }

    // #7 Monday -> Wednesday -> Friday (one rest day between each) -> 3
    [Fact]
    public void OneRestDayBetweenEachWorkout_StreakOfThree()
    {
        var result = StreakCalculator.Compute([Monday, Wednesday, Friday], TimeZoneInfo.Utc, Friday);

        Assert.Equal(3, result.CurrentStreak);
    }

    // #8 Monday -> Thursday (two consecutive rest days: Tue + Wed) -> resets to 1
    [Fact]
    public void MondayToThursday_ResetsToOne()
    {
        var result = StreakCalculator.Compute([Monday, Thursday], TimeZoneInfo.Utc, Thursday);

        Assert.Equal(1, result.CurrentStreak);
    }

    [Fact]
    public void MultipleWorkoutsOnATransitDay_StillCollapseToOneDay()
    {
        // Monday, Tuesday, Tuesday (again), Wednesday -> 3 distinct qualifying days.
        var result = StreakCalculator.Compute(
            [Monday, Tuesday, Tuesday.AddHours(6), Wednesday], TimeZoneInfo.Utc, Wednesday);

        Assert.Equal(3, result.CurrentStreak);
    }

    // #9 Saturday 8am -> Monday 9am (49 hours) -> continues. This is the direct proof
    // that hours are irrelevant now: 49h > the old 48h threshold, but the calendar-day
    // gap is still just one rest day (Sunday), so the streak must continue regardless.
    [Fact]
    public void Saturday8amToMonday9am_49HoursApart_StillContinues()
    {
        var saturday8am = new DateTime(2026, 6, 13, 8, 0, 0, DateTimeKind.Utc);
        var monday9am = new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(49, (monday9am - saturday8am).TotalHours);

        var result = StreakCalculator.Compute([saturday8am, monday9am], TimeZoneInfo.Utc, monday9am);

        Assert.Equal(2, result.CurrentStreak);
    }

    // #10 Timezone boundary: two workouts on different UTC calendar dates that fall on
    // the SAME local calendar date under the user's real IANA timezone. Asia/Kolkata
    // (UTC+5:30, no DST) is used for a fully deterministic, season-independent offset.
    [Fact]
    public void TimezoneBoundary_UtcDateDiffersFromLocalDate_UsesLocalDateForBucketing()
    {
        // 2026-06-14T19:00Z and 2026-06-14T20:00Z are the same UTC date, but +5:30
        // pushes both past local midnight into 2026-06-15 -- still the same local day.
        var t1 = new DateTime(2026, 6, 14, 19, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 14, 20, 0, 0, DateTimeKind.Utc);
        var kolkata = StreakCalculator.ResolveTimeZone("Asia/Kolkata");

        var localResult = StreakCalculator.Compute([t1, t2], kolkata, t2);

        Assert.Equal(1, localResult.CurrentStreak);
        Assert.Equal(new DateOnly(2026, 6, 15), localResult.LastWorkoutLocalDate);
    }

    [Fact]
    public void TimezoneBoundary_SameUtcDate_DifferentLocalDatesUnderKolkata_CountsAsTwoDays()
    {
        // 2026-06-14T17:00Z (Kolkata: 2026-06-14 22:30, still June 14 local) and
        // 2026-06-14T19:00Z (Kolkata: 2026-06-15 00:30, now June 15 local) -- same
        // UTC date, but different LOCAL dates, so this should be two qualifying days.
        var t1 = new DateTime(2026, 6, 14, 17, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 14, 19, 0, 0, DateTimeKind.Utc);
        var kolkata = StreakCalculator.ResolveTimeZone("Asia/Kolkata");

        var result = StreakCalculator.Compute([t1, t2], kolkata, t2);

        Assert.Equal(2, result.CurrentStreak);
    }

    [Fact]
    public void SessionsSpanningUtcMidnight_CountAsTwoDistinctDaysButStreakContinues()
    {
        // Only 20 minutes apart in real time, but straddling UTC midnight (no timezone
        // configured, so UTC is the calendar-day boundary).
        var beforeMidnight = new DateTime(2026, 6, 14, 23, 50, 0, DateTimeKind.Utc);
        var afterMidnight = new DateTime(2026, 6, 15, 0, 10, 0, DateTimeKind.Utc);

        var result = StreakCalculator.Compute([beforeMidnight, afterMidnight], TimeZoneInfo.Utc, afterMidnight);

        Assert.Equal(2, result.CurrentStreak);
    }

    [Fact]
    public void LongInactivity_StreakIsZeroAsOfNow()
    {
        var tenDaysAgo = Monday;
        var now = Monday.AddDays(10);

        var result = StreakCalculator.Compute([tenDaysAgo], TimeZoneInfo.Utc, now);

        Assert.Equal(0, result.CurrentStreak);
    }

    // #11 Best streak remains unchanged after a reset
    [Fact]
    public void BestStreak_SurvivesACurrentStreakReset()
    {
        var best = StreakCalculator.UpdateBestStreak(currentStreak: 1, existingBest: 3);

        Assert.Equal(3, best);
    }

    [Fact]
    public void BestStreak_UpdatesWhenCurrentExceedsIt()
    {
        var best = StreakCalculator.UpdateBestStreak(currentStreak: 4, existingBest: 3);

        Assert.Equal(4, best);
    }

    [Fact]
    public void BestStreak_MonWedFriExample_ThenAGapKeepsBestAtThree()
    {
        // Mon -> Wed -> Fri legitimately reaches 3 (one rest day each time).
        var chain = StreakCalculator.Compute([Monday, Wednesday, Friday], TimeZoneInfo.Utc, Friday);
        var best = StreakCalculator.UpdateBestStreak(chain.CurrentStreak, existingBest: 0);
        Assert.Equal(3, best);

        // Several days pass with no workout, then one more workout starts fresh at 1.
        var nextWorkout = Friday.AddDays(10);
        var afterGap = StreakCalculator.Compute([Monday, Wednesday, Friday, nextWorkout], TimeZoneInfo.Utc, nextWorkout);
        var bestAfterGap = StreakCalculator.UpdateBestStreak(afterGap.CurrentStreak, best);

        Assert.Equal(1, afterGap.CurrentStreak);
        Assert.Equal(3, bestAfterGap); // best must NOT drop to 1
    }

    // #12 Freshness calculation for an old persisted streak (read-time, no session
    // re-query — EffectiveCurrentStreak works from the persisted anchor alone).
    [Fact]
    public void EffectiveCurrentStreak_ReturnsZero_WhenAllowedRestWindowHasBeenExceeded()
    {
        var lastQualifying = Monday;
        var now = Monday.AddDays(3); // gap of 3 calendar days -> two rest days exceeded

        var effective = StreakCalculator.EffectiveCurrentStreak(
            persistedCurrentStreak: 5, lastQualifying, timeZoneId: null, now);

        Assert.Equal(0, effective);
    }

    [Fact]
    public void EffectiveCurrentStreak_ReturnsPersistedValue_WhenStillWithinAllowedRestWindow()
    {
        var lastQualifying = Monday;
        var now = Monday.AddDays(2); // one rest day -- still within the allowance

        var effective = StreakCalculator.EffectiveCurrentStreak(
            persistedCurrentStreak: 5, lastQualifying, timeZoneId: null, now);

        Assert.Equal(5, effective);
    }

    [Fact]
    public void EffectiveCurrentStreak_ReturnsZero_WhenNeverSynced()
    {
        var effective = StreakCalculator.EffectiveCurrentStreak(
            persistedCurrentStreak: 0, lastQualifyingWorkoutAtUtc: null, timeZoneId: null, Monday);

        Assert.Equal(0, effective);
    }

    [Fact]
    public void EffectiveCurrentStreak_UsesExplicitIanaTimeZoneId_ForTheRestWindowCheck()
    {
        // Kolkata is UTC+5:30. lastQualifying = 2026-06-14T19:00Z (local: 2026-06-15
        // 00:30). now = 2026-06-16T19:30Z (local: 2026-06-17 01:00) -- local dates are
        // June 15 and June 17, a 2-day gap, still within the allowance.
        var lastQualifying = new DateTime(2026, 6, 14, 19, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 16, 19, 30, 0, DateTimeKind.Utc);

        var effective = StreakCalculator.EffectiveCurrentStreak(
            persistedCurrentStreak: 4, lastQualifying, timeZoneId: "Asia/Kolkata", now);

        Assert.Equal(4, effective);
    }

    // #13 Null TimeZoneId falls back to UTC (existing behavior preserved)
    [Fact]
    public void NullTimeZoneId_FallsBackToUtcForCompute()
    {
        var resolved = StreakCalculator.ResolveTimeZone(null);
        Assert.Equal(TimeZoneInfo.Utc, resolved);

        var result = StreakCalculator.Compute([Saturday, Monday], resolved, Monday);
        Assert.Equal(2, result.CurrentStreak); // identical to the explicit-UTC test above
    }

    [Fact]
    public void NullTimeZoneId_FallsBackToUtcForEffectiveCurrentStreak()
    {
        var lastQualifying = Monday;
        var now = Monday.AddDays(2);

        var effective = StreakCalculator.EffectiveCurrentStreak(
            persistedCurrentStreak: 5, lastQualifying, timeZoneId: null, now);

        Assert.Equal(5, effective); // matches the UTC-explicit "still within window" case
    }

    [Fact]
    public void UnknownTimeZoneId_AlsoFallsBackToUtc()
    {
        var resolved = StreakCalculator.ResolveTimeZone("Not/A_Real_Zone");
        Assert.Equal(TimeZoneInfo.Utc, resolved);
    }

    [Fact]
    public void Leaderboard_SortsByEffectiveStreak_NotTheRawStaleColumn()
    {
        // User A: raw CurrentStreak=10 but hasn't worked out in 3+ days (rest window
        // exceeded -> effectively 0). User B: raw CurrentStreak=3, worked out today.
        var now = Monday.AddDays(3);
        var userAEffective = StreakCalculator.EffectiveCurrentStreak(
            persistedCurrentStreak: 10, lastQualifyingWorkoutAtUtc: Monday, timeZoneId: null, now);
        var userBEffective = StreakCalculator.EffectiveCurrentStreak(
            persistedCurrentStreak: 3, lastQualifyingWorkoutAtUtc: now, timeZoneId: null, now);

        var ranked = new[] { ("A", userAEffective), ("B", userBEffective) }
            .OrderByDescending(x => x.Item2)
            .Select(x => x.Item1)
            .ToList();

        Assert.Equal(0, userAEffective);
        Assert.Equal(3, userBEffective);
        Assert.Equal(["B", "A"], ranked);
    }
}
