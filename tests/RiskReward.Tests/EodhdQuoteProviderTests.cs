using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class EodhdQuoteProviderTests
{
    [Fact]
    public async Task ReadsCanadianPriceAndProviderTimestamp()
    {
        var timestamp = DateTimeOffset.Parse("2026-09-24T14:15:00Z");
        var handler = new StubHandler($$"""
            {"code":"GEO.TO","timestamp":{{timestamp.ToUnixTimeSeconds()}},"close":2.80,"previousClose":2.75,"change":0.05,"change_p":1.8182}
            """);
        var provider = new EodhdQuoteProvider(new HttpClient(handler),
            Options.Create(new ProviderOptions { EodhdApiKey = "test-key" }));

        var quotes = await provider.GetQuotesAsync(new Dictionary<string, string> { ["GEODF"] = "GEO.TO" });

        var quote = Assert.Single(quotes).Value;
        Assert.Equal(2.80m, quote.Price);
        Assert.Equal("CAD", quote.Currency);
        Assert.Equal(timestamp, quote.QuotedAt);
        Assert.True(quote.TimestampIsProviderSupplied);
        Assert.Equal(2.75m, quote.PreviousClose);
        Assert.Equal(0.05m, quote.DailyChange);
        Assert.Equal(1.8182m, quote.DailyChangePercent);
        Assert.Contains("GEO.TO", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("api_token=test-key", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task RequestsUsListingsWithTheUsExchangeCode()
    {
        var handler = new StubHandler("""
            {"code":"DBOXF.US","timestamp":1794428940,"gmtoffset":0,"open":0.52,"high":0.64,"low":0.52,"close":0.604,"volume":324301,"previousClose":0.7525,"change":-0.1485,"change_p":-19.7342}
            """);
        var provider = new EodhdQuoteProvider(new HttpClient(handler),
            Options.Create(new ProviderOptions { EodhdApiKey = "test-key" }));

        var quotes = await provider.GetQuotesAsync(new Dictionary<string, string> { ["DBOXF"] = "DBOXF" });

        var quote = Assert.Single(quotes).Value;
        Assert.Equal(0.604m, quote.Price);
        Assert.Equal("USD", quote.Currency);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794428940), quote.QuotedAt);
        Assert.EndsWith("/DBOXF.US", handler.LastRequest!.RequestUri!.AbsolutePath);
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
