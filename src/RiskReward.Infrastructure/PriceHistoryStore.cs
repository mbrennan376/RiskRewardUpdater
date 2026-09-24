using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

public sealed class PriceHistoryStore
{
    private readonly RiskRewardOptions options;
    private readonly ILogger<PriceHistoryStore> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    public PriceHistoryStore(IOptions<RiskRewardOptions> configured, ILogger<PriceHistoryStore> logger)
    {
        options = configured.Value;
        this.logger = logger;
    }

    public async Task UpdateAsync(
        DeploymentTarget target,
        ChartCatalog catalog,
        IReadOnlyDictionary<string, Quote> freshQuotes,
        CancellationToken cancellationToken = default)
    {
        if (freshQuotes.Count == 0) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await ReadManifestAsync(target, cancellationToken);
            var manifestChanged = false;
            foreach (var chart in catalog.Charts)
            {
                if (!freshQuotes.TryGetValue(chart.TickerSymbol, out var quote) || quote.IsStale || quote.Price <= 0) continue;
                var symbol = SafeSymbol(MarketMetadata.ActiveSymbol(chart));
                var currency = MarketMetadata.NormalizeCurrency(chart.Currency, symbol);
                var exchange = MarketMetadata.InferExchange(chart.Exchange, symbol);
                var timestamp = quote.QuotedAt.ToUniversalTime();
                var filename = timestamp.ToString("yyyy-MM") + ".json";
                var path = $"history/{symbol}/{filename}";

                manifest.TryGetValue(symbol, out var entry);
                if (entry is not null && timestamp < entry.Last)
                {
                    logger.LogWarning("History {Symbol}: timestamp={Timestamp:O} file={File} action=ignored-older", symbol, timestamp, filename);
                    continue;
                }

                var file = await ReadHistoryFileAsync(target, path, cancellationToken) ?? new HistoricalPriceFile
                {
                    Symbol = symbol,
                    Currency = currency,
                    Exchange = exchange
                };
                file.Symbol = symbol;
                file.Currency = currency;
                file.Exchange = exchange;
                var existingIndex = file.Points.FindIndex(point => point.Timestamp == timestamp);
                string action;
                if (existingIndex >= 0)
                {
                    if (file.Points[existingIndex].Price == quote.Price)
                    {
                        logger.LogDebug("History {Symbol}: timestamp={Timestamp:O} file={File} action=ignored-duplicate", symbol, timestamp, filename);
                        continue;
                    }
                    file.Points[existingIndex] = new HistoricalPricePoint(timestamp, quote.Price);
                    action = "replace";
                }
                else
                {
                    file.Points.Add(new HistoricalPricePoint(timestamp, quote.Price));
                    file.Points.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
                    action = "append";
                }

                await WriteHistoryFileAsync(target, path, file, timestamp, cancellationToken);
                var first = file.Points[0].Timestamp;
                var last = file.Points[^1].Timestamp;
                entry ??= new HistoryManifestEntry { First = first, Last = last };
                entry.Currency = currency;
                entry.Exchange = exchange;
                if (entry.First == default || first < entry.First) entry.First = first;
                if (last > entry.Last) entry.Last = last;
                if (!entry.Files.Contains(filename, StringComparer.OrdinalIgnoreCase)) entry.Files.Add(filename);
                entry.Files.Sort(StringComparer.OrdinalIgnoreCase);
                manifest[symbol] = entry;
                manifestChanged = true;
                logger.LogInformation("History {Symbol}: provider={Provider} timestamp={Timestamp:O} price={Price} file={File} action={Action}",
                    symbol, quote.Provider, timestamp, quote.Price, filename, action);
            }

            if (manifestChanged) await WriteManifestAsync(target, manifest, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<Dictionary<string, HistoryManifestEntry>> ReadManifestAsync(DeploymentTarget target, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await OpenReadAsync(target, "history/manifest.json", cancellationToken);
            if (stream is null) return new(StringComparer.OrdinalIgnoreCase);
            var value = await JsonSerializer.DeserializeAsync<Dictionary<string, HistoryManifestEntry>>(stream, JsonDefaults.Options, cancellationToken);
            return value is null ? new(StringComparer.OrdinalIgnoreCase) : new(value, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "The history manifest is invalid; history was not modified.");
            throw;
        }
    }

    private async Task<HistoricalPriceFile?> ReadHistoryFileAsync(DeploymentTarget target, string path, CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadAsync(target, path, cancellationToken);
        return stream is null ? null : await JsonSerializer.DeserializeAsync<HistoricalPriceFile>(stream, JsonDefaults.Options, cancellationToken);
    }

    private async Task<Stream?> OpenReadAsync(DeploymentTarget target, string relativePath, CancellationToken cancellationToken)
    {
        if (target == DeploymentTarget.Local)
        {
            var path = Path.Combine(options.LocalSitePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
        try
        {
            return (await LiveContainer().GetBlobClient(relativePath).DownloadStreamingAsync(cancellationToken: cancellationToken)).Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    private Task WriteManifestAsync(DeploymentTarget target, Dictionary<string, HistoryManifestEntry> manifest, CancellationToken cancellationToken) =>
        WriteJsonAsync(target, "history/manifest.json", manifest, "no-cache", cancellationToken);

    private Task WriteHistoryFileAsync(DeploymentTarget target, string path, HistoricalPriceFile file, DateTimeOffset observation, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var currentMonth = observation.Year == now.Year && observation.Month == now.Month;
        return WriteJsonAsync(target, path, file, currentMonth ? "public, max-age=300" : "public, max-age=31536000, immutable", cancellationToken);
    }

    private async Task WriteJsonAsync<T>(DeploymentTarget target, string relativePath, T value, string cacheControl, CancellationToken cancellationToken)
    {
        if (target == DeploymentTarget.Local)
        {
            var path = Path.Combine(options.LocalSitePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options, cancellationToken);
            File.Move(temporary, path, true);
            return;
        }

        await using var content = new MemoryStream();
        await JsonSerializer.SerializeAsync(content, value, JsonDefaults.Options, cancellationToken);
        content.Position = 0;
        await LiveContainer().GetBlobClient(relativePath).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json", CacheControl = cacheControl }
        }, cancellationToken);
    }

    private BlobContainerClient LiveContainer()
    {
        if (string.IsNullOrWhiteSpace(options.AzureStorageConnectionString)) throw new InvalidOperationException("Azure Storage is not configured.");
        return new BlobContainerClient(options.AzureStorageConnectionString, options.LiveContainerName);
    }

    private static string SafeSymbol(string symbol)
    {
        var normalized = new string(symbol.Trim().ToUpperInvariant().Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-').ToArray());
        if (string.IsNullOrWhiteSpace(normalized)) throw new InvalidOperationException("A history symbol contained no safe characters.");
        return normalized;
    }
}
