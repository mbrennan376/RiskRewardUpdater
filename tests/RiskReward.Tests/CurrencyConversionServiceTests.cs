using System.Net;
using System.Text;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class CurrencyConversionServiceTests
{
    [Fact]
    public async Task ReadsDailyBankOfCanadaRateAndReturnsItsInverse()
    {
        var handler = new StubHandler("""
            {"observations":[{"d":"2026-09-23","FXUSDCAD":{"v":"1.3500"}}]}
            """);
        var service = new CurrencyConversionService(new HttpClient(handler));

        var usdCad = await service.GetRateAsync("usd", "cad");
        var cadUsd = await service.GetRateAsync("CAD", "USD");

        Assert.Equal(1.3500m, usdCad.Rate);
        Assert.Equal(new DateOnly(2026, 9, 23), usdCad.EffectiveDate);
        Assert.Equal(decimal.Round(1m / 1.3500m, 10), cadUsd.Rate);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
