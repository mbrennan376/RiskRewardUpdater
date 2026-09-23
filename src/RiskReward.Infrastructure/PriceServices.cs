using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

public static class MarketSchedule
{
    public static bool IsPollingSlot(DateTimeOffset easternTime) =>
        easternTime.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
        easternTime.TimeOfDay >= new TimeSpan(9, 30, 0) && easternTime.TimeOfDay <= new TimeSpan(16, 0, 0) &&
        easternTime.Minute % 15 == 0;

    public static string SlotKey(DateTimeOffset easternTime) => easternTime.ToString("yyyy-MM-dd-HH-mm");
}

public static class ChartCatalogReader
{
    public static async Task<ChartCatalog> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
            return document.RootElement.Deserialize<ChartCatalog>(JsonDefaults.Options) ?? new ChartCatalog();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Chart data must be a catalog object or a legacy chart array.");

        var catalog = new ChartCatalog();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var ticker = Read(row, "tickerSymbol", "TickerSymbol", "Ticker Symbol").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(ticker)) continue;
            DateOnly.TryParse(Read(row, "updatedDate", "UpdatedDate", "Updated Date"), out var updated);
            catalog.Charts.Add(new RiskRewardChart
            {
                TickerSymbol = ticker,
                CompanyName = Read(row, "companyName", "CompanyName", "Company Name") is { Length: > 0 } company ? company : ticker,
                UpdatedDate = updated,
                ChartFilename = Read(row, "chartFilename", "ChartFilename", "Chart Filename"),
                Comments = Read(row, "comments", "Comments")
            });
        }
        return catalog;
    }

    private static string Read(JsonElement row, params string[] names)
    {
        foreach (var name in names)
            if (row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        return "";
    }
}

public sealed class PriceUpdateService
{
    private readonly IReadOnlyList<IQuoteProvider> providers;
    private readonly RiskRewardOptions options;
    private readonly StateStore stateStore;
    private readonly ILogger<PriceUpdateService> logger;
    private int nextProvider;

    public PriceUpdateService(IEnumerable<IQuoteProvider> providers, IOptions<RiskRewardOptions> options, StateStore stateStore, ILogger<PriceUpdateService> logger)
    {
        this.providers = providers.ToList();
        this.options = options.Value;
        this.stateStore = stateStore;
        this.logger = logger;
    }

    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (providers.Count < 2) throw new InvalidOperationException("Configure two quote providers.");
        var target = await stateStore.GetTargetAsync(cancellationToken);
        if (target == DeploymentTarget.Live && !options.AllowLivePublishing)
        {
            logger.LogWarning("Live target was requested but live publishing is disabled; prices were not updated.");
            return;
        }
        var catalog = await ReadCatalogAsync(target, cancellationToken);
        if (catalog.Charts.Count == 0) { logger.LogInformation("No published charts were found."); return; }

        var primary = providers[nextProvider++ % providers.Count];
        var fallback = providers[(nextProvider) % providers.Count];
        var primarySymbols = BuildSymbols(catalog, primary.Name);
        var quotes = new Dictionary<string, Quote>(await GetQuotesSafelyAsync(primary, primarySymbols, cancellationToken), StringComparer.OrdinalIgnoreCase);
        var missing = catalog.Charts.Where(chart => !quotes.ContainsKey(chart.TickerSymbol)).ToList();
        if (missing.Count > 0)
        {
            logger.LogWarning("{Provider} missed {Count} symbols; falling back to {Fallback}.", primary.Name, missing.Count, fallback.Name);
            var fallbackCatalog = new ChartCatalog { Charts = missing };
            foreach (var pair in await GetQuotesSafelyAsync(fallback, BuildSymbols(fallbackCatalog, fallback.Name), cancellationToken)) quotes[pair.Key] = pair.Value;
        }

        var previous = await ReadPricesAsync(target, cancellationToken);
        foreach (var chart in catalog.Charts)
        {
            if (quotes.ContainsKey(chart.TickerSymbol)) continue;
            if (previous.Quotes.TryGetValue(chart.TickerSymbol, out var old)) quotes[chart.TickerSymbol] = old with { IsStale = true, Error = "Both quote providers failed." };
        }
        await WritePricesAsync(target, new PriceCatalog { GeneratedAt = DateTimeOffset.UtcNow, Quotes = quotes }, cancellationToken);
        logger.LogInformation("Published {Count}/{Total} prices to {Target}; primary provider was {Provider}.", quotes.Count, catalog.Charts.Count, target, primary.Name);
    }

    private async Task<IReadOnlyDictionary<string, Quote>> GetQuotesSafelyAsync(
        IQuoteProvider provider,
        IReadOnlyDictionary<string, string> symbols,
        CancellationToken cancellationToken)
    {
        try { return await provider.GetQuotesAsync(symbols, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quote provider {Provider} failed; the next provider will be used for its missing symbols.", provider.Name);
            return new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> BuildSymbols(ChartCatalog catalog, string provider) => catalog.Charts.ToDictionary(
        chart => chart.TickerSymbol,
        chart => chart.ProviderSymbols.TryGetValue(provider, out var mapped) && !string.IsNullOrWhiteSpace(mapped) ? mapped : chart.TickerSymbol,
        StringComparer.OrdinalIgnoreCase);

    private async Task<ChartCatalog> ReadCatalogAsync(DeploymentTarget target, CancellationToken cancellationToken)
    {
        if (target == DeploymentTarget.Local)
        {
            var path = Path.Combine(options.LocalSitePath, "data.json");
            if (!File.Exists(path)) return new ChartCatalog();
            await using var stream = File.OpenRead(path);
            return await ChartCatalogReader.ReadAsync(stream, cancellationToken);
        }
        var blob = LiveContainer().GetBlobClient("data.json");
        var download = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
        await using var content = download.Value.Content;
        return await ChartCatalogReader.ReadAsync(content, cancellationToken);
    }

    private async Task<PriceCatalog> ReadPricesAsync(DeploymentTarget target, CancellationToken cancellationToken)
    {
        try
        {
            Stream stream;
            if (target == DeploymentTarget.Local)
            {
                var path = Path.Combine(options.LocalSitePath, "prices.json");
                if (!File.Exists(path)) return new PriceCatalog();
                stream = File.OpenRead(path);
            }
            else stream = (await LiveContainer().GetBlobClient("prices.json").DownloadStreamingAsync(cancellationToken: cancellationToken)).Value.Content;
            await using (stream) return await JsonSerializer.DeserializeAsync<PriceCatalog>(stream, JsonDefaults.Options, cancellationToken) ?? new PriceCatalog();
        }
        catch { return new PriceCatalog(); }
    }

    private async Task WritePricesAsync(DeploymentTarget target, PriceCatalog prices, CancellationToken cancellationToken)
    {
        if (target == DeploymentTarget.Local)
        {
            Directory.CreateDirectory(options.LocalSitePath);
            var path = Path.Combine(options.LocalSitePath, "prices.json");
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, prices, JsonDefaults.Options, cancellationToken);
            File.Move(temporary, path, true);
            return;
        }
        await using var content = new MemoryStream();
        await JsonSerializer.SerializeAsync(content, prices, JsonDefaults.Options, cancellationToken); content.Position = 0;
        await LiveContainer().GetBlobClient("prices.json").UploadAsync(content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/json", CacheControl = "no-cache" } }, cancellationToken);
    }

    private BlobContainerClient LiveContainer()
    {
        if (string.IsNullOrWhiteSpace(options.AzureStorageConnectionString)) throw new InvalidOperationException("Azure Storage is not configured.");
        return new BlobContainerClient(options.AzureStorageConnectionString, options.LiveContainerName);
    }
}
