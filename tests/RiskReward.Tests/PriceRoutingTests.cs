using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RiskReward.Core;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class PriceRoutingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"risk-reward-routing-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset LongAgo = DateTimeOffset.Parse("2020-01-02T21:00:00Z");

    [Fact]
    public async Task RoutesEachListingToItsFreshestProviderWithoutConfiguration()
    {
        var finnhub = new FakeProvider("finnhub", symbol => symbol switch
        {
            "NVDA" => Fresh("finnhub", 175m),
            // Finnhub's free plan only has an end-of-day price for OTC listings.
            "DBOXF" => new Quote("DBOXF", 0.60m, LongAgo.AddDays(-1), "finnhub", IsEndOfDayOnly: true),
            _ => null
        });
        var eodhd = new FakeProvider("eodhd", symbol => symbol switch
        {
            "GSI.V" => Fresh("eodhd", 1.62m, "CAD"),
            // No trade yet today: EODHD's latest trade is from an earlier session.
            "DBOXF" => new Quote("DBOXF", 0.59m, LongAgo, "eodhd"),
            _ => null
        });
        var twelveData = new FakeProvider("twelveData", _ => null);
        var service = CreateService(dailyEodhdCalls: 0, twelveData, finnhub, eodhd);

        var first = await service.UpdateAsync();

        // Cycle 1: the Canadian listing goes straight to EODHD, everything else to Finnhub, and the listing
        // Finnhub only had an end-of-day price for falls through to EODHD in the same cycle.
        Assert.Equal([["GSI.V"], ["DBOXF"]], eodhd.Requests);
        Assert.Equal([["DBOXF", "NVDA"]], finnhub.Requests);
        Assert.Empty(twelveData.Requests);
        Assert.True(first.AllAnswered);
        var prices = await ReadPrices();
        Assert.Equal("finnhub", prices.Quotes["NVDA"].Provider);
        Assert.Equal("eodhd", prices.Quotes["GKPRF"].Provider);
        Assert.Equal("eodhd", prices.Quotes["DBOXF"].Provider);
        Assert.Equal(0.59m, prices.Quotes["DBOXF"].Price);
        // EODHD confirmed the latest trade, so an earlier-session price is not stale.
        Assert.All(prices.Quotes.Values, quote => Assert.False(quote.IsStale));

        await service.UpdateAsync();

        // Cycle 2: the learned OTC listing now goes to EODHD first and Finnhub is not asked for it.
        Assert.Equal(["DBOXF", "GSI.V"], eodhd.Requests[2]);
        Assert.Equal(3, eodhd.Requests.Count);
        Assert.Equal(["NVDA"], finnhub.Requests[1]);
        Assert.Empty(twelveData.Requests);
    }

    [Fact]
    public async Task FallsBackToEodhdThenTwelveDataWhenFinnhubMissesAListedStock()
    {
        var finnhub = new FakeProvider("finnhub", _ => null);
        var eodhd = new FakeProvider("eodhd", symbol => symbol is "GSI.V" or "DBOXF" ? Fresh("eodhd", 1m, symbol == "GSI.V" ? "CAD" : "USD") : null);
        var twelveData = new FakeProvider("twelveData", symbol => symbol == "NVDA" ? Fresh("twelveData", 175m) : null);
        var service = CreateService(dailyEodhdCalls: 0, twelveData, finnhub, eodhd);

        var result = await service.UpdateAsync();

        Assert.Equal([["GSI.V"], ["DBOXF", "NVDA"]], eodhd.Requests);
        Assert.Equal([["NVDA"]], twelveData.Requests);
        Assert.True(result.AllAnswered);
        Assert.Equal("twelveData", (await ReadPrices()).Quotes["NVDA"].Provider);
    }

    [Fact]
    public async Task MarksAnEarlierSessionQuoteStaleWhenNoSameDaySourceAnswered()
    {
        var finnhub = new FakeProvider("finnhub", symbol => symbol switch
        {
            "NVDA" => Fresh("finnhub", 175m),
            "DBOXF" => new Quote("DBOXF", 0.60m, LongAgo, "finnhub", IsEndOfDayOnly: true),
            _ => null
        });
        var eodhd = new FakeProvider("eodhd", symbol => symbol == "GSI.V" ? Fresh("eodhd", 1.62m, "CAD") : null);
        var service = CreateService(dailyEodhdCalls: 0, new FakeProvider("twelveData", _ => null), finnhub, eodhd);

        var result = await service.UpdateAsync();

        var prices = await ReadPrices();
        Assert.True(prices.Quotes["DBOXF"].IsStale);
        Assert.False(prices.Quotes["NVDA"].IsStale);
        Assert.Equal(1, result.LaggingCount);
    }

    [Fact]
    public async Task PublishesTodaysUsHolidayAndDoesNotFlagThePreviousClose()
    {
        var today = DateOnly.FromDateTime(MarketSchedule.ToEastern(DateTimeOffset.UtcNow).Date);
        var holidays = $$"""{"data":[{"eventName":"Test Holiday","atDate":"{{today:yyyy-MM-dd}}","tradingHour":""}],"exchange":"US"}""";
        var calendar = new MarketCalendarService(new HttpClient(new JsonHandler(holidays)),
            Options.Create(new ProviderOptions { FinnhubApiKey = "test-key" }),
            Options.Create(new RiskRewardOptions { StateFolder = Path.Combine(root, "state") }), NullLogger<MarketCalendarService>.Instance);
        // The last trading day's close, as every provider reports it on a holiday.
        var lastClose = MarketSchedule.EffectiveQuoteTime(DateTimeOffset.UtcNow, new HashSet<DateOnly> { today });
        var finnhub = new FakeProvider("finnhub", symbol => symbol is "NVDA" or "DBOXF" ? new Quote("", 1m, lastClose, "finnhub") : null);
        var eodhd = new FakeProvider("eodhd", symbol => symbol == "GSI.V" ? Fresh("eodhd", 1.62m, "CAD") : null);
        var service = CreateService(dailyEodhdCalls: 0, calendar, new FakeProvider("twelveData", _ => null), finnhub, eodhd);

        var result = await service.UpdateAsync();

        var prices = await ReadPrices();
        Assert.Equal(new MarketHolidayNotice(today, "Test Holiday"), prices.MarketHoliday);
        Assert.All(prices.Quotes.Values, quote => Assert.False(quote.IsStale));
        Assert.Equal(0, result.LaggingCount);
        // Nothing is from an earlier session on a holiday, so EODHD is only asked for its own listing.
        Assert.Equal([["GSI.V"]], eodhd.Requests);
    }

    private static Quote Fresh(string provider, decimal price, string currency = "USD") =>
        new("", price, DateTimeOffset.UtcNow, provider, Currency: currency);

    private PriceUpdateService CreateService(int dailyEodhdCalls, params IQuoteProvider[] providers) =>
        CreateService(dailyEodhdCalls, null, providers);

    private PriceUpdateService CreateService(int dailyEodhdCalls, MarketCalendarService? calendar, params IQuoteProvider[] providers)
    {
        var site = Path.Combine(root, "site");
        Directory.CreateDirectory(site);
        var catalog = new ChartCatalog
        {
            Charts =
            [
                new RiskRewardChart { TickerSymbol = "NVDA", CompanyName = "NVIDIA", Currency = "USD", UpperLine = 215, LowerLine = 50 },
                new RiskRewardChart { TickerSymbol = "DBOXF", CompanyName = "D-Box", Currency = "USD", UpperLine = 1.7m, LowerLine = 0.5m },
                new RiskRewardChart
                {
                    TickerSymbol = "GKPRF", CompanyName = "Gatekeeper", Currency = "CAD", UpperLine = 4, LowerLine = 0.4m,
                    CurrencyTickerSymbols = new(StringComparer.OrdinalIgnoreCase) { ["USD"] = "GKPRF", ["CAD"] = "GSI.V" }
                }
            ]
        };
        File.WriteAllText(Path.Combine(site, "data.json"), JsonSerializer.Serialize(catalog, JsonDefaults.Options));
        var options = Options.Create(new RiskRewardOptions { StateFolder = Path.Combine(root, "state"), LocalSitePath = site });
        return new PriceUpdateService(providers, options, new StateStore(options), NullLogger<PriceUpdateService>.Instance,
            new ProviderRunCsvLogger(options), new PriceHistoryStore(options, NullLogger<PriceHistoryStore>.Instance),
            new CurrencyConversionService(new HttpClient(new OfflineHandler())),
            Options.Create(new ProviderOptions { EodhdDailyCallLimit = dailyEodhdCalls }), calendar);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private async Task<PriceCatalog> ReadPrices()
    {
        await using var stream = File.OpenRead(Path.Combine(root, "site", "prices.json"));
        return (await JsonSerializer.DeserializeAsync<PriceCatalog>(stream, JsonDefaults.Options))!;
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeProvider(string name, Func<string, Quote?> answer) : IQuoteProvider
    {
        public string Name => name;
        public List<string[]> Requests { get; } = [];

        public Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(
            IReadOnlyDictionary<string, string> tickerToProviderSymbol, CancellationToken cancellationToken = default)
        {
            Requests.Add(tickerToProviderSymbol.Values.OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray());
            var result = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in tickerToProviderSymbol)
                if (answer(pair.Value) is { } quote) result[pair.Key] = quote with { Ticker = pair.Key };
            return Task.FromResult<IReadOnlyDictionary<string, Quote>>(result);
        }
    }

    // The exchange-rate lookup must not reach the network from a test.
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable);
    }
}
