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
}
