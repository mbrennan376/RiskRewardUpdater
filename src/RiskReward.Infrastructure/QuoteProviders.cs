using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

public sealed class TwelveDataQuoteProvider : IQuoteProvider
{
    private readonly HttpClient http;
    private readonly ProviderOptions options;
    public string Name => "twelveData";

    public TwelveDataQuoteProvider(HttpClient http, IOptions<ProviderOptions> options)
    {
        this.http = http;
        this.options = options.Value;
        this.http.Timeout = TimeSpan.FromSeconds(this.options.RequestTimeoutSeconds);
    }

    public async Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(IReadOnlyDictionary<string, string> symbols, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(options.TwelveDataApiKey)) return result;
        var chunks = symbols.Chunk(8).ToList();
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var requested = string.Join(',', chunk.Select(pair => pair.Value));
            using var response = await http.GetAsync($"https://api.twelvedata.com/quote?symbol={Uri.EscapeDataString(requested)}&apikey={Uri.EscapeDataString(options.TwelveDataApiKey)}", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var pair in chunk)
            {
                var node = chunk.Length == 1 ? document.RootElement : FindCaseInsensitive(document.RootElement, pair.Value);
                if (node is not { } value || !TryDecimal(value, "close", out var price) || price <= 0) continue;
                // Twelve Data's `timestamp` is the opening time of the requested bar. For the
                // default daily interval that is normally 9:30 AM, regardless of when the quote
                // was updated. Prefer its current last-quote/update fields for the observation.
                var suppliedTimestamp = TryTimestamp(value, "last_quote_at", out var timestamp) ||
                    TryTimestamp(value, "last_update_at", out timestamp) ||
                    TryTimestamp(value, "timestamp", out timestamp);
                if (!suppliedTimestamp) timestamp = MarketSchedule.EffectiveQuoteTime(DateTimeOffset.UtcNow);
                TryDecimal(value, "previous_close", out var previousClose);
                TryDecimal(value, "change", out var change);
                TryDecimal(value, "percent_change", out var changePercent);
                var currency = value.TryGetProperty("currency", out var currencyNode) && !string.IsNullOrWhiteSpace(currencyNode.GetString())
                    ? currencyNode.GetString()!.Trim().ToUpperInvariant()
                    : SymbolCurrency(pair.Value);
                result[pair.Key] = new Quote(pair.Key, price, timestamp, Name,
                    Currency: currency, TimestampIsProviderSupplied: suppliedTimestamp,
                    PreviousClose: previousClose > 0 ? previousClose : null,
                    DailyChange: previousClose > 0 ? change : null,
                    DailyChangePercent: previousClose > 0 ? changePercent : null);
            }
            if (index < chunks.Count - 1) await Task.Delay(TimeSpan.FromSeconds(61), cancellationToken);
        }
        return result;
    }

    private static JsonElement? FindCaseInsensitive(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static bool TryDecimal(JsonElement root, string name, out decimal value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var node)) return false;
        return node.ValueKind == JsonValueKind.Number
            ? node.TryGetDecimal(out value)
            : decimal.TryParse(node.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryTimestamp(JsonElement root, string name, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!root.TryGetProperty(name, out var node)) return false;

        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var numeric))
            return TryUnixTimestamp(numeric, out timestamp);

        if (node.ValueKind != JsonValueKind.String) return false;
        var raw = node.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            return TryUnixTimestamp(numeric, out timestamp);

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    private static bool TryUnixTimestamp(long value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value <= 0) return false;
        try
        {
            timestamp = value > 32_503_680_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string SymbolCurrency(string symbol) =>
        symbol.Contains(":TSX", StringComparison.OrdinalIgnoreCase) || symbol.EndsWith(".TO", StringComparison.OrdinalIgnoreCase) || symbol.EndsWith(".V", StringComparison.OrdinalIgnoreCase) ? "CAD" : "USD";
}

public sealed class FinnhubQuoteProvider : IQuoteProvider
{
    private readonly HttpClient http;
    private readonly ProviderOptions options;
    public string Name => "finnhub";

    public FinnhubQuoteProvider(HttpClient http, IOptions<ProviderOptions> options)
    {
        this.http = http;
        this.options = options.Value;
        this.http.Timeout = TimeSpan.FromSeconds(this.options.RequestTimeoutSeconds);
    }

    public async Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(IReadOnlyDictionary<string, string> symbols, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(options.FinnhubApiKey)) return result;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, options.FinnhubCycleTimeoutSeconds));
        foreach (var pair in symbols)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(remaining < TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
                ? remaining
                : TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            try
            {
                using var response = await http.GetAsync($"https://finnhub.io/api/v1/quote?symbol={Uri.EscapeDataString(pair.Value)}&token={Uri.EscapeDataString(options.FinnhubApiKey)}", requestCancellation.Token);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(requestCancellation.Token));
                if (!document.RootElement.TryGetProperty("c", out var current) || current.GetDecimal() <= 0) continue;
                long unix = 0;
                var suppliedTimestamp = document.RootElement.TryGetProperty("t", out var time) && time.TryGetInt64(out unix) && unix > 0;
                var timestamp = suppliedTimestamp ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow;
                var price = current.GetDecimal();
                var previousClose = ReadDecimal(document.RootElement, "pc");
                var change = ReadDecimal(document.RootElement, "d");
                var changePercent = ReadDecimal(document.RootElement, "dp");
                result[pair.Key] = new Quote(pair.Key, price, timestamp, Name,
                    Currency: SymbolCurrency(pair.Value), TimestampIsProviderSupplied: suppliedTimestamp,
                    PreviousClose: previousClose > 0 ? previousClose : null,
                    DailyChange: previousClose > 0 ? change : null,
                    DailyChangePercent: previousClose > 0 ? changePercent : null);
            }
            catch (HttpRequestException) { /* Missing quotes are retried through the fallback provider. */ }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* Keep the whole provider pass bounded. */ }
            remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(1050) ? remaining : TimeSpan.FromMilliseconds(1050), cancellationToken);
        }
        return result;
    }

    private static string SymbolCurrency(string symbol) =>
        symbol.Contains(":TSX", StringComparison.OrdinalIgnoreCase) || symbol.EndsWith(".TO", StringComparison.OrdinalIgnoreCase) || symbol.EndsWith(".V", StringComparison.OrdinalIgnoreCase) ? "CAD" : "USD";

    private static decimal ReadDecimal(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number && node.TryGetDecimal(out var value) ? value : 0;
}

public sealed class EodhdQuoteProvider : IQuoteProvider
{
    private readonly HttpClient http;
    private readonly ProviderOptions options;
    public string Name => "eodhd";

    public EodhdQuoteProvider(HttpClient http, IOptions<ProviderOptions> options)
    {
        this.http = http;
        this.options = options.Value;
        this.http.Timeout = TimeSpan.FromSeconds(this.options.RequestTimeoutSeconds);
    }

    public async Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(
        IReadOnlyDictionary<string, string> symbols,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(options.EodhdApiKey)) return result;

        foreach (var pair in symbols)
        {
            try
            {
                var symbol = pair.Value.Trim().ToUpperInvariant();
                using var response = await http.GetAsync(
                    $"https://eodhd.com/api/real-time/{Uri.EscapeDataString(symbol)}?api_token={Uri.EscapeDataString(options.EodhdApiKey)}&fmt=json",
                    cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (!TryDecimal(document.RootElement, "close", out var price) || price <= 0) continue;
                long unix = 0;
                var suppliedTimestamp = document.RootElement.TryGetProperty("timestamp", out var time) &&
                    time.TryGetInt64(out unix) && unix > 0;
                var timestamp = suppliedTimestamp ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow;
                TryDecimal(document.RootElement, "previousClose", out var previousClose);
                TryDecimal(document.RootElement, "change", out var change);
                TryDecimal(document.RootElement, "change_p", out var changePercent);
                result[pair.Key] = new Quote(pair.Key, price, timestamp, Name,
                    Currency: SymbolCurrency(symbol), TimestampIsProviderSupplied: suppliedTimestamp,
                    PreviousClose: previousClose > 0 ? previousClose : null,
                    DailyChange: previousClose > 0 ? change : null,
                    DailyChangePercent: previousClose > 0 ? changePercent : null);
            }
            catch (HttpRequestException) { /* A missing symbol is retried through the next adapter. */ }
            catch (JsonException) { /* An invalid provider response is treated as a missing quote. */ }
        }
        return result;
    }

    private static bool TryDecimal(JsonElement root, string name, out decimal value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var node)) return false;
        return node.ValueKind == JsonValueKind.Number
            ? node.TryGetDecimal(out value)
            : decimal.TryParse(node.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string SymbolCurrency(string symbol) =>
        symbol.EndsWith(".TO", StringComparison.OrdinalIgnoreCase) || symbol.EndsWith(".V", StringComparison.OrdinalIgnoreCase)
            ? "CAD"
            : "USD";
}
