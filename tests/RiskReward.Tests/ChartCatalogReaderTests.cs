using System.Text;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class ChartCatalogReaderTests
{
    [Fact]
    public async Task ReadsLegacyTopLevelArray()
    {
        const string json = """
            [{"Ticker Symbol":"TPCS","Company Name":"TechPrecision Corp","Updated Date":"2024-08-01","Chart Filename":"charts/tpcs.jpg","Comments":"Legacy"}]
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = await ChartCatalogReader.ReadAsync(stream);

        var chart = Assert.Single(catalog.Charts);
        Assert.Equal("TPCS", chart.TickerSymbol);
        Assert.Equal("TechPrecision Corp", chart.CompanyName);
        Assert.Equal("charts/tpcs.jpg", chart.ChartFilename);
    }

    [Fact]
    public async Task ReadsVersionedCatalogObject()
    {
        const string json = """
            {"schemaVersion":2,"charts":[{"tickerSymbol":"AEHR","companyName":"Aehr","upperLine":94,"lowerLine":10,"providerSymbols":{"finnhub":"AEHR"}}]}
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = await ChartCatalogReader.ReadAsync(stream);

        var chart = Assert.Single(catalog.Charts);
        Assert.Equal(94m, chart.UpperLine);
        Assert.Equal("AEHR", chart.ProviderSymbols["finnhub"]);
    }
}
