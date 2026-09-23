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
}
