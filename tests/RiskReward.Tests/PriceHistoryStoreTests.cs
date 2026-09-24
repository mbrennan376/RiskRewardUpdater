using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RiskReward.Core;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class PriceHistoryStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"risk-reward-history-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreatesNewTickerHistoryAndCompactManifest()
    {
        var store = CreateStore();
        var timestamp = DateTimeOffset.Parse("2026-09-24T13:30:00Z");

        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(timestamp, 1.67m));

        var history = await ReadHistory("GSI.V", "2026-09.json");
        var manifest = await ReadManifest();
        Assert.Single(history.Points);
        Assert.Equal(new HistoricalPricePoint(timestamp, 1.67m), history.Points[0]);
        Assert.Equal("CAD", history.Currency);
        Assert.Equal("TSXV", history.Exchange);
        Assert.Equal(timestamp, manifest["GSI.V"].First);
        Assert.Equal(["2026-09.json"], manifest["GSI.V"].Files);
        var json = (await File.ReadAllTextAsync(HistoryPath("GSI.V", "2026-09.json"))).Replace("\r\n", "\n");
        Assert.Contains("[\n      \"2026-09-24T13:30:00Z\",\n      1.67\n    ]", json);
    }

    [Fact]
    public async Task AppendsNewerPointWithoutDuplicatingSameTimestamp()
    {
        var store = CreateStore();
        var first = DateTimeOffset.Parse("2026-09-24T13:30:00Z");
        var second = first.AddMinutes(15);
        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(first, 1.67m));
        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(second, 1.68m));
        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(second, 1.68m));

        var history = await ReadHistory("GSI.V", "2026-09.json");
        Assert.Equal(2, history.Points.Count);
        Assert.Equal(second, (await ReadManifest())["GSI.V"].Last);
    }

    [Fact]
    public async Task ReplacesChangedValueAtSameTimestamp()
    {
        var store = CreateStore();
        var timestamp = DateTimeOffset.Parse("2026-09-24T13:30:00Z");
        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(timestamp, 1.67m));
        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(timestamp, 1.69m));

        var history = await ReadHistory("GSI.V", "2026-09.json");
        Assert.Single(history.Points);
        Assert.Equal(1.69m, history.Points[0].Price);
    }

    [Fact]
    public async Task IgnoresOlderTimestamp()
    {
        var store = CreateStore();
        var newest = DateTimeOffset.Parse("2026-09-24T14:00:00Z");
        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(newest, 1.70m));
        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(newest.AddMinutes(-15), 1.60m));

        var history = await ReadHistory("GSI.V", "2026-09.json");
        Assert.Single(history.Points);
        Assert.Equal(1.70m, history.Points[0].Price);
    }

    [Fact]
    public async Task StartsNewMonthAndPreservesOldMonth()
    {
        var store = CreateStore();
        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(DateTimeOffset.Parse("2026-09-30T19:45:00Z"), 1.70m));
        await store.UpdateAsync(DeploymentTarget.Local, Catalog(), Quotes(DateTimeOffset.Parse("2026-10-01T13:30:00Z"), 1.71m));

        Assert.True(File.Exists(HistoryPath("GSI.V", "2026-09.json")));
        Assert.True(File.Exists(HistoryPath("GSI.V", "2026-10.json")));
        Assert.Equal(["2026-09.json", "2026-10.json"], (await ReadManifest())["GSI.V"].Files);
    }

    private PriceHistoryStore CreateStore() => new(Options.Create(new RiskRewardOptions { LocalSitePath = root }), NullLogger<PriceHistoryStore>.Instance);

    private static ChartCatalog Catalog() => new()
    {
        Charts =
        [
            new RiskRewardChart
            {
                TickerSymbol = "GSI",
                Currency = "CAD",
                CurrencyTickerSymbols = new(StringComparer.OrdinalIgnoreCase) { ["CAD"] = "GSI.V", ["USD"] = "GKPRF" }
            }
        ]
    };

    private static Dictionary<string, Quote> Quotes(DateTimeOffset timestamp, decimal price) =>
        new(StringComparer.OrdinalIgnoreCase) { ["GSI"] = new Quote("GSI", price, timestamp, "test", Currency: "CAD") };

    private string HistoryPath(string symbol, string file) => Path.Combine(root, "history", symbol, file);
    private async Task<HistoricalPriceFile> ReadHistory(string symbol, string file) =>
        JsonSerializer.Deserialize<HistoricalPriceFile>(await File.ReadAllTextAsync(HistoryPath(symbol, file)), JsonDefaults.Options)!;
    private async Task<Dictionary<string, HistoryManifestEntry>> ReadManifest() =>
        JsonSerializer.Deserialize<Dictionary<string, HistoryManifestEntry>>(await File.ReadAllTextAsync(Path.Combine(root, "history", "manifest.json")), JsonDefaults.Options)!;

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
