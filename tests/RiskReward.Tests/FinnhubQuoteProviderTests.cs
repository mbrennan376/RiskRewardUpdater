using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class FinnhubQuoteProviderTests
{
    [Fact]
    public async Task FlagsAMidnightStampAsAnEndOfDayOnlyQuote()
    {
        // 2026-11-10T00:00:00Z: a date, not a trade time. The price is that session's close.
        var quote = await GetQuote("""{"c":0.7525,"d":-0.0475,"dp":-5.9375,"pc":0.8,"t":1794268800}""");

        Assert.True(quote.IsEndOfDayOnly);
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T16:00:00-05:00"), quote.QuotedAt);
        Assert.Equal(0.7525m, quote.Price);
    }

    [Fact]
    public async Task KeepsARealTradeTime()
    {
        // 2026-11-11T20:15:05Z
        var quote = await GetQuote("""{"c":760.31,"d":1.2,"dp":0.16,"pc":759.11,"t":1794428105}""");

        Assert.False(quote.IsEndOfDayOnly);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794428105), quote.QuotedAt);
    }

    private static async Task<RiskReward.Core.Quote> GetQuote(string json)
    {
        var provider = new FinnhubQuoteProvider(new HttpClient(new StubHandler(json)),
            Options.Create(new ProviderOptions { FinnhubApiKey = "test-key" }));
        var quotes = await provider.GetQuotesAsync(new Dictionary<string, string> { ["TEST"] = "TEST" });
        return Assert.Single(quotes).Value;
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
