using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

public static class MarketSchedule
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public static bool IsPollingSlot(DateTimeOffset easternTime) =>
        easternTime.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
        easternTime.TimeOfDay >= new TimeSpan(9, 30, 0) && easternTime.TimeOfDay <= new TimeSpan(16, 0, 0) &&
        easternTime.Minute % 15 == 0;

    public static string SlotKey(DateTimeOffset easternTime) => easternTime.ToString("yyyy-MM-dd-HH-mm");

    public static bool IsFinalRetryWindow(DateTimeOffset easternTime) =>
        easternTime.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
        easternTime.TimeOfDay >= new TimeSpan(16, 0, 0) && easternTime.TimeOfDay <= new TimeSpan(16, 30, 0);

    public static DateTimeOffset EffectiveQuoteTime(DateTimeOffset utcNow)
    {
        var easternNow = TimeZoneInfo.ConvertTime(utcNow, Eastern);
        var weekday = easternNow.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
        if (weekday && easternNow.TimeOfDay >= new TimeSpan(9, 30, 0) && easternNow.TimeOfDay < new TimeSpan(16, 0, 0))
            return utcNow;

        var closeDate = easternNow.Date;
        if (!weekday || easternNow.TimeOfDay < new TimeSpan(16, 0, 0)) closeDate = closeDate.AddDays(-1);
        while (closeDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) closeDate = closeDate.AddDays(-1);
        var localClose = DateTime.SpecifyKind(closeDate.AddHours(16), DateTimeKind.Unspecified);
        return new DateTimeOffset(localClose, Eastern.GetUtcOffset(localClose));
    }
}

public sealed record PriceUpdateResult(int FreshCount, int TotalCount, DeploymentTarget Target)
{
    public bool AllFresh => TotalCount > 0 && FreshCount == TotalCount;
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
    private readonly ProviderRunCsvLogger providerRuns;
    private int nextProvider;

    public PriceUpdateService(IEnumerable<IQuoteProvider> providers, IOptions<RiskRewardOptions> options, StateStore stateStore, ILogger<PriceUpdateService> logger, ProviderRunCsvLogger providerRuns)
    {
        this.providers = providers.ToList();
        this.options = options.Value;
        this.stateStore = stateStore;
        this.logger = logger;
        this.providerRuns = providerRuns;
    }

    public async Task<PriceUpdateResult> UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (providers.Count < 2) throw new InvalidOperationException("Configure two quote providers.");
        try { providerRuns.DeleteLogsOlderThan(TimeSpan.FromDays(30)); }
        catch (Exception ex) { logger.LogWarning(ex, "Old price-service logs could not be cleaned up."); }
        var target = await stateStore.GetTargetAsync(cancellationToken);
        if (target == DeploymentTarget.Live && !options.AllowLivePublishing)
        {
            logger.LogWarning("Live target was requested but live publishing is disabled; prices were not updated.");
            return new PriceUpdateResult(0, 0, target);
        }
        var catalog = await ReadCatalogAsync(target, cancellationToken);
        if (catalog.Charts.Count == 0) { logger.LogInformation("No published charts were found."); return new PriceUpdateResult(0, 0, target); }

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

        var freshCount = quotes.Count;
        var previous = await ReadPricesAsync(target, cancellationToken);
        foreach (var chart in catalog.Charts)
        {
            if (quotes.ContainsKey(chart.TickerSymbol)) continue;
            if (previous.Quotes.TryGetValue(chart.TickerSymbol, out var old)) quotes[chart.TickerSymbol] = old with { IsStale = true, Error = "Both quote providers failed." };
        }
        await WritePricesAsync(target, new PriceCatalog { GeneratedAt = DateTimeOffset.UtcNow, Quotes = quotes }, cancellationToken);
        logger.LogInformation("Published {Count}/{Total} prices to {Target}, including {FreshCount} fresh quotes; primary provider was {Provider}.", quotes.Count, catalog.Charts.Count, target, freshCount, primary.Name);
        return new PriceUpdateResult(freshCount, catalog.Charts.Count, target);
    }

    private async Task<IReadOnlyDictionary<string, Quote>> GetQuotesSafelyAsync(
        IQuoteProvider provider,
        IReadOnlyDictionary<string, string> symbols,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, Quote> result = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        try
        {
            result = await provider.GetQuotesAsync(symbols, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quote provider {Provider} failed; the next provider will be used for its missing symbols.", provider.Name);
            return result;
        }
        finally
        {
            try { providerRuns.Record(provider.Name, symbols.Count, result.Count); }
            catch (Exception ex) { logger.LogWarning(ex, "The provider summary CSV could not be written."); }
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
