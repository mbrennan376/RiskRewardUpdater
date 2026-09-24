using RiskReward.Core;

namespace RiskReward.Tests;

public sealed class MarketMetadataTests
{
    [Fact]
    public void SelectsTheTickerForTheActiveChartCurrency()
    {
        var chart = new RiskRewardChart
        {
            TickerSymbol = "GSI",
            Currency = "CAD",
            CurrencyTickerSymbols = new(StringComparer.OrdinalIgnoreCase)
            {
                ["USD"] = "GKPRF",
                ["CAD"] = "GSI.V"
            }
        };

        Assert.Equal("GSI.V", MarketMetadata.ActiveSymbol(chart));
        Assert.Equal("TSXV", MarketMetadata.InferExchange(null, MarketMetadata.ActiveSymbol(chart)));

        chart.Currency = "USD";
        Assert.Equal("GKPRF", MarketMetadata.ActiveSymbol(chart));
    }
}
