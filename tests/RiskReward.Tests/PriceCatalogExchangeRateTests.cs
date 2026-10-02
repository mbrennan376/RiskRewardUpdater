using System.Text.Json;
using RiskReward.Core;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class PriceCatalogExchangeRateTests
{
    [Fact]
    public void SerializesPublishedUsdCadRateForStaticClients()
    {
        var catalog = new PriceCatalog
        {
            ExchangeRates = new Dictionary<string, PublishedExchangeRate>(StringComparer.OrdinalIgnoreCase)
            {
                ["USDCAD"] = new("USD", "CAD", 1.35m, new DateOnly(2026, 9, 29), "Bank of Canada Valet")
            }
        };

        var json = JsonSerializer.Serialize(catalog, JsonDefaults.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rate = root.GetProperty("exchangeRates").GetProperty("USDCAD");

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1.35m, rate.GetProperty("rate").GetDecimal());
        Assert.Equal("2026-09-29", rate.GetProperty("effectiveDate").GetString());
        Assert.False(rate.GetProperty("isStale").GetBoolean());
    }

    [Fact]
    public void OlderPriceCatalogWithoutRatesGetsEmptyCollection()
    {
        var catalog = JsonSerializer.Deserialize<PriceCatalog>("""{"schemaVersion":1,"quotes":{}}""", JsonDefaults.Options);

        Assert.NotNull(catalog);
        Assert.Empty(catalog.ExchangeRates);
    }
}
