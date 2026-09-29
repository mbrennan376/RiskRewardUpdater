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

    [Fact]
    public void RejectsAPreviousQuoteAfterTheChartCurrencyChanges()
    {
        var chart = new RiskRewardChart { TickerSymbol = "GEODF", Currency = "CAD" };
        var previousUsdQuote = new Quote("GEODF", 1.85m, DateTimeOffset.UtcNow, "finnhub", Currency: "USD");
        var newCadQuote = previousUsdQuote with { Price = 2.80m, Currency = "CAD" };

        Assert.False(MarketMetadata.QuoteMatchesChartCurrency(chart, previousUsdQuote));
        Assert.True(MarketMetadata.QuoteMatchesChartCurrency(chart, newCadQuote));
    }
}
