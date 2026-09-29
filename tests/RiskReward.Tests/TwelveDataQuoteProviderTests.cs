using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class TwelveDataQuoteProviderTests
{
    [Fact]
    public async Task PrefersLastQuoteTimestampAndReadsDailyMovementFromQuoteEndpoint()
    {
        var barOpen = DateTimeOffset.Parse("2026-09-24T13:30:00Z");
        var lastQuote = DateTimeOffset.Parse("2026-09-24T14:15:00Z");
        var handler = new StubHandler($$"""
            {"symbol":"AEHR","currency":"USD","timestamp":{{barOpen.ToUnixTimeSeconds()}},"last_quote_at":{{lastQuote.ToUnixTimeSeconds()}},"close":"95.55","previous_close":"97.38","change":"-1.83","percent_change":"-1.8792"}
            """);
        var provider = new TwelveDataQuoteProvider(new HttpClient(handler),
            Options.Create(new ProviderOptions { TwelveDataApiKey = "test-key" }));

        var quotes = await provider.GetQuotesAsync(new Dictionary<string, string> { ["AEHR"] = "AEHR" });

        var quote = Assert.Single(quotes).Value;
        Assert.Equal(95.55m, quote.Price);
        Assert.Equal(97.38m, quote.PreviousClose);
        Assert.Equal(-1.83m, quote.DailyChange);
        Assert.Equal(-1.8792m, quote.DailyChangePercent);
        Assert.Equal(lastQuote, quote.QuotedAt);
        Assert.Contains("/quote", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ReadsIsoLastUpdateTimestampWhenLastQuoteTimestampIsMissing()
    {
        var barOpen = DateTimeOffset.Parse("2026-09-24T13:30:00Z");
        var lastUpdate = DateTimeOffset.Parse("2026-09-24T14:22:17Z");
        var handler = new StubHandler($$"""
            {"symbol":"AEHR","currency":"USD","timestamp":{{barOpen.ToUnixTimeSeconds()}},"last_update_at":"{{lastUpdate:O}}","close":"95.55"}
            """);
        var provider = new TwelveDataQuoteProvider(new HttpClient(handler),
            Options.Create(new ProviderOptions { TwelveDataApiKey = "test-key" }));

        var quote = Assert.Single(await provider.GetQuotesAsync(
            new Dictionary<string, string> { ["AEHR"] = "AEHR" })).Value;

        Assert.Equal(lastUpdate, quote.QuotedAt);
    }

    [Fact]
    public async Task FallsBackToBarTimestampForLegacyResponses()
    {
        var barOpen = DateTimeOffset.Parse("2026-09-24T13:30:00Z");
        var handler = new StubHandler($$"""
            {"symbol":"AEHR","currency":"USD","timestamp":{{barOpen.ToUnixTimeSeconds()}},"close":"95.55"}
            """);
        var provider = new TwelveDataQuoteProvider(new HttpClient(handler),
            Options.Create(new ProviderOptions { TwelveDataApiKey = "test-key" }));

        var quote = Assert.Single(await provider.GetQuotesAsync(
            new Dictionary<string, string> { ["AEHR"] = "AEHR" })).Value;

        Assert.Equal(barOpen, quote.QuotedAt);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
