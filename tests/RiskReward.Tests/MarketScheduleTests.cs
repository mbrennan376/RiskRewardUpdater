using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class MarketScheduleTests
{
    [Theory]
    [InlineData(2026, 9, 23, 9, 30, true)]
    [InlineData(2026, 9, 23, 9, 45, true)]
    [InlineData(2026, 9, 23, 16, 0, true)]
    [InlineData(2026, 9, 23, 9, 31, false)]
    [InlineData(2026, 9, 23, 16, 15, false)]
    [InlineData(2026, 9, 26, 10, 0, false)]
    public void IdentifiesQuarterHourWeekdaySlots(int year, int month, int day, int hour, int minute, bool expected)
    {
        var value = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(-4));
        Assert.Equal(expected, MarketSchedule.IsPollingSlot(value));
    }

    [Theory]
    [InlineData(2026, 9, 23, 16, 0, true)]
    [InlineData(2026, 9, 23, 16, 25, true)]
    [InlineData(2026, 9, 23, 16, 30, true)]
    [InlineData(2026, 9, 23, 16, 31, false)]
    [InlineData(2026, 9, 26, 16, 15, false)]
    public void IdentifiesFinalCloseRetryWindow(int year, int month, int day, int hour, int minute, bool expected)
    {
        var value = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(-4));
        Assert.Equal(expected, MarketSchedule.IsFinalRetryWindow(value));
    }

    [Theory]
    [InlineData("2026-09-23T20:40:00Z", "2026-09-23T16:00:00-04:00")]
    [InlineData("2026-09-23T12:00:00Z", "2026-09-22T16:00:00-04:00")]
    [InlineData("2026-09-26T15:00:00Z", "2026-09-25T16:00:00-04:00")]
    public void UsesLatestWeekdayCloseOutsideMarketHours(string utcValue, string expected)
    {
        var actual = MarketSchedule.EffectiveQuoteTime(DateTimeOffset.Parse(utcValue));
        Assert.Equal(DateTimeOffset.Parse(expected), actual);
    }

    [Fact]
    public void UsesSchedulerSlotDuringMarketHours()
    {
        var observed = DateTimeOffset.Parse("2026-09-23T15:08:37Z");
        Assert.Equal(DateTimeOffset.Parse("2026-09-23T11:00:00-04:00"), MarketSchedule.EffectiveQuoteTime(observed));
    }

    [Theory]
    [InlineData("2026-09-24T15:15:00Z", "2026-09-24T16:00:00Z", true)]
    [InlineData("2026-09-23T20:00:00Z", "2026-09-24T16:00:00Z", false)]
    [InlineData("2026-09-25T20:00:00Z", "2026-09-26T16:00:00Z", true)]
    public void ValidatesTheApplicableTradingSession(string observation, string now, bool expected)
    {
        Assert.Equal(expected, MarketSchedule.IsCurrentMarketSession(
            DateTimeOffset.Parse(observation), DateTimeOffset.Parse(now)));
    }

    // 2026-11-11 is a Wednesday in Eastern Standard Time (UTC-5).
    [Theory]
    [InlineData("2026-11-10T21:00:00Z", "2026-11-11T20:15:00Z", true)]   // yesterday's close at 3:15 PM
    [InlineData("2026-11-11T16:15:00Z", "2026-11-11T20:15:00Z", false)]  // traded this morning
    [InlineData("2026-11-10T21:00:00Z", "2026-11-11T14:45:00Z", false)]  // first hour: no trade yet is normal
    [InlineData("2026-11-09T21:00:00Z", "2026-11-11T14:45:00Z", true)]   // first hour, but two sessions old
    [InlineData("2026-11-10T21:00:00Z", "2026-11-11T13:00:00Z", false)]  // before the open
    [InlineData("2026-11-10T21:00:00Z", "2026-11-11T21:30:00Z", true)]   // after today's close
    [InlineData("2026-11-06T21:00:00Z", "2026-11-07T15:00:00Z", false)]  // weekend uses Friday's close
    public void FlagsQuotesFromAnEarlierSession(string observation, string now, bool expected)
    {
        Assert.Equal(expected, MarketSchedule.IsLaggingQuote(
            DateTimeOffset.Parse(observation), DateTimeOffset.Parse(now)));
    }

    [Fact]
    public void OpeningGraceCanBeTurnedOffForProvidersWithSameDayQuotes()
    {
        var yesterdayClose = DateTimeOffset.Parse("2026-11-11T21:00:00Z");
        var firstHour = DateTimeOffset.Parse("2026-11-12T14:54:00Z"); // 9:54 AM Eastern
        Assert.False(MarketSchedule.IsLaggingQuote(yesterdayClose, firstHour));
        Assert.True(MarketSchedule.IsLaggingQuote(yesterdayClose, firstHour, allowOpeningGrace: false));
        Assert.False(MarketSchedule.IsLaggingQuote(DateTimeOffset.Parse("2026-11-12T14:40:00Z"), firstHour, allowOpeningGrace: false));
    }

    [Fact]
    public void TreatsMidnightUtcStampAsThatDatesClose()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T16:00:00-05:00"),
            MarketSchedule.NormalizeDateOnlyTimestamp(DateTimeOffset.Parse("2026-11-10T00:00:00Z")));
        var intraday = DateTimeOffset.Parse("2026-11-11T19:10:20Z");
        Assert.Equal(intraday, MarketSchedule.NormalizeDateOnlyTimestamp(intraday));
    }

    [Theory]
    [InlineData("2026-11-11T09:30:00-05:00", false)]
    [InlineData("2026-11-11T10:02:00-05:00", true)]   // a slow primary pass still lands in the 10:00 slot
    [InlineData("2026-11-11T10:15:00-05:00", false)]
    [InlineData("2026-11-11T10:45:00-05:00", true)]
    [InlineData("2026-11-11T14:30:00-05:00", true)]
    [InlineData("2026-11-11T15:45:00-05:00", false)]
    [InlineData("2026-11-11T16:00:00-05:00", true)]   // the close is always fetched
    [InlineData("2026-11-11T08:00:00-05:00", true)]   // startup refresh outside the session
    [InlineData("2026-11-14T11:00:00-05:00", true)]   // weekend
    public void SpreadsTwentyEodhdCallsForTwoSymbolsAcrossTheSession(string eastern, bool expected)
    {
        Assert.Equal(expected, EodhdBudget.AllowsCycle(DateTimeOffset.Parse(eastern), 2, 20));
    }

    [Theory]
    [InlineData("2026-11-13T09:30:00-05:00", true)]
    [InlineData("2026-11-13T09:35:00-05:00", true)]
    [InlineData("2026-11-13T09:37:00-05:00", false)]
    [InlineData("2026-11-13T16:00:00-05:00", true)]
    [InlineData("2026-11-13T16:05:00-05:00", false)]
    [InlineData("2026-11-14T10:00:00-05:00", false)]
    public void IdentifiesFiveMinuteSlots(string eastern, bool expected)
    {
        Assert.Equal(expected, MarketSchedule.IsPollingSlot(DateTimeOffset.Parse(eastern), 5));
    }

    [Fact]
    public void RationedEodhdSpendsOnlyOncePerQuarterHourWithFiveMinuteCycles()
    {
        Assert.True(EodhdBudget.AllowsCycle(DateTimeOffset.Parse("2026-11-11T10:00:00-05:00"), 2, 20));
        Assert.False(EodhdBudget.AllowsCycle(DateTimeOffset.Parse("2026-11-11T10:05:00-05:00"), 2, 20));
        Assert.False(EodhdBudget.AllowsCycle(DateTimeOffset.Parse("2026-11-11T10:10:00-05:00"), 2, 20));
    }

    [Fact]
    public void EodhdBudgetNeverExceedsTheDailyLimitInOneSession()
    {
        var open = DateTimeOffset.Parse("2026-11-11T09:30:00-05:00");
        foreach (var symbols in new[] { 1, 2, 3, 4, 7, 20 })
        {
            var cycles = Enumerable.Range(0, 27).Count(slot => EodhdBudget.AllowsCycle(open.AddMinutes(slot * 15), symbols, 20));
            Assert.InRange(cycles, 1, 27);
            Assert.True(cycles * symbols <= 20, $"{symbols} symbols used {cycles * symbols} calls");
        }
    }

    [Fact]
    public void EodhdBudgetIsNotRationedOnAnUnlimitedPlan()
    {
        var open = DateTimeOffset.Parse("2026-11-11T09:30:00-05:00");
        Assert.True(EodhdBudget.IsUnlimited(0, 4));
        Assert.True(EodhdBudget.IsUnlimited(100_000, 4));
        Assert.False(EodhdBudget.IsUnlimited(20, 2));
        Assert.All(Enumerable.Range(0, 27), slot => Assert.True(EodhdBudget.AllowsCycle(open.AddMinutes(slot * 15), 4, 0)));
    }
}
