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
            using var response = await http.GetAsync($"https://api.twelvedata.com/price?symbol={Uri.EscapeDataString(requested)}&apikey={Uri.EscapeDataString(options.TwelveDataApiKey)}", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var pair in chunk)
            {
                var node = chunk.Length == 1 ? document.RootElement : FindCaseInsensitive(document.RootElement, pair.Value);
                if (node is { } value && value.TryGetProperty("price", out var priceNode) && decimal.TryParse(priceNode.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price) && price > 0)
                    result[pair.Key] = new Quote(pair.Key, price, DateTimeOffset.UtcNow, Name, Currency: SymbolCurrency(pair.Value));
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
        foreach (var pair in symbols)
        {
            try
            {
                using var response = await http.GetAsync($"https://finnhub.io/api/v1/quote?symbol={Uri.EscapeDataString(pair.Value)}&token={Uri.EscapeDataString(options.FinnhubApiKey)}", cancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (!document.RootElement.TryGetProperty("c", out var current) || current.GetDecimal() <= 0) continue;
                var timestamp = document.RootElement.TryGetProperty("t", out var time) && time.TryGetInt64(out var unix) && unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow;
                result[pair.Key] = new Quote(pair.Key, current.GetDecimal(), timestamp, Name, Currency: SymbolCurrency(pair.Value));
            }
            catch (HttpRequestException) { /* Missing quotes are retried through the fallback provider. */ }
            await Task.Delay(TimeSpan.FromMilliseconds(1050), cancellationToken);
        }
        return result;
    }

    private static string SymbolCurrency(string symbol) =>
        symbol.Contains(":TSX", StringComparison.OrdinalIgnoreCase) || symbol.EndsWith(".TO", StringComparison.OrdinalIgnoreCase) || symbol.EndsWith(".V", StringComparison.OrdinalIgnoreCase) ? "CAD" : "USD";
}
