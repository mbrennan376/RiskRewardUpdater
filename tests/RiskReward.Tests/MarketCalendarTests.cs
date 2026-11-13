using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class MarketCalendarTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"risk-reward-calendar-{Guid.NewGuid():N}");

    // Finnhub's documented answer shape.
    private const string UsHolidays = """
        {
          "data": [
            { "eventName": "Thanksgiving Day", "atDate": "2026-11-26", "tradingHour": "" },
            { "eventName": "Day after Thanksgiving", "atDate": "2026-11-27", "tradingHour": "09:30-13:00" },
            { "eventName": "Christmas", "atDate": "2026-12-25", "tradingHour": "" }
          ],
          "exchange": "US",
          "timezone": "America/New_York"
        }
        """;

    private static readonly IReadOnlySet<DateOnly> Thanksgiving = new HashSet<DateOnly> { new(2026, 11, 26) };

    [Fact]
    public void ParsesFinnhubHolidaysAndKeepsShortenedDaysAsTradingDays()
    {
        var calendar = new MarketCalendar(new Dictionary<string, List<MarketHoliday>> { ["US"] = MarketCalendarService.Parse(UsHolidays) });

        Assert.Equal(new[] { new DateOnly(2026, 11, 26), new DateOnly(2026, 12, 25) }, calendar.UsClosed.OrderBy(date => date).ToArray());
        Assert.Equal("Thanksgiving Day", calendar.UsHolidayOn(new DateOnly(2026, 11, 26))!.Name);
        Assert.Null(calendar.UsHolidayOn(new DateOnly(2026, 11, 27)));
        Assert.Empty(calendar.CanadaClosed);
    }

    [Theory]
    [InlineData("""{"error":"You don't have access to this resource."}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""[]""")]
    public void AnUnexpectedAnswerHasNoHolidays(string json)
    {
        Assert.Empty(MarketCalendarService.Parse(json));
    }

    // 2026-11-25 is the Wednesday before Thanksgiving; 21:00Z is its 4:00 PM Eastern close.
    [Theory]
    [InlineData("2026-11-26T16:00:00Z", true, false)]   // Thanksgiving 11:00 AM: Wednesday's close is current
    [InlineData("2026-11-26T16:00:00Z", false, true)]   // without the calendar it was flagged
    [InlineData("2026-11-26T22:00:00Z", true, false)]   // Thanksgiving evening
    [InlineData("2026-11-27T14:45:00Z", true, false)]   // Friday, first hour: the last session was Wednesday
    [InlineData("2026-11-27T16:00:00Z", true, true)]    // Friday 11:00 AM: Wednesday's close is now old
    public void WednesdaysCloseAroundThanksgiving(string now, bool withCalendar, bool expectedLagging)
    {
        var wednesdayClose = DateTimeOffset.Parse("2026-11-25T21:00:00Z");
        Assert.Equal(expectedLagging, MarketSchedule.IsLaggingQuote(wednesdayClose, DateTimeOffset.Parse(now), true, withCalendar ? Thanksgiving : null));
    }

    [Fact]
    public void AHolidayIsNotATradingSession()
    {
        var thanksgivingNoon = DateTimeOffset.Parse("2026-11-26T17:00:00Z");
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T16:00:00-05:00"), MarketSchedule.EffectiveQuoteTime(thanksgivingNoon, Thanksgiving));
        Assert.False(MarketSchedule.IsTradingDay(new DateTime(2026, 11, 26), Thanksgiving));
        Assert.True(MarketSchedule.IsTradingDay(new DateTime(2026, 11, 27), Thanksgiving));
    }

    [Fact]
    public async Task LoadsOncePerDaySavesTheCalendarAndSurvivesAFailedLoadAfterRestart()
    {
        var online = new StubHandler(request => request.RequestUri!.Query.Contains("exchange=US") ? UsHolidays : """{"data":[]}""");
        var first = CreateService(online);

        var calendar = await first.GetAsync();
        await first.GetAsync();

        Assert.Contains(new DateOnly(2026, 11, 26), calendar.UsClosed);
        Assert.Equal(3, online.Calls); // US, TO and V once; the second call reuses the day's answer
        Assert.True(File.Exists(Path.Combine(root, "market-holidays.json")));

        // A restarted service whose Finnhub call fails still has the saved calendar.
        var restarted = CreateService(new StubHandler(_ => throw new HttpRequestException("offline")));
        Assert.Contains(new DateOnly(2026, 11, 26), (await restarted.GetAsync()).UsClosed);
    }

    private MarketCalendarService CreateService(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(new ProviderOptions { FinnhubApiKey = "test-key" }),
            Options.Create(new RiskRewardOptions { StateFolder = root }), NullLogger<MarketCalendarService>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request), Encoding.UTF8, "application/json") });
        }
    }
}
