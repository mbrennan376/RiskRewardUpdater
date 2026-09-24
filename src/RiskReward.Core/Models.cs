namespace RiskReward.Core;

public enum DeploymentTarget { Local, Live }

public sealed class RiskRewardChart
{
    public string TickerSymbol { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public DateOnly UpdatedDate { get; set; }
    public string ChartFilename { get; set; } = "";
    public string ImageHash { get; set; } = "";
    public decimal? UpperLine { get; set; }
    public decimal? LowerLine { get; set; }
    public string Currency { get; set; } = "";
    public string? Exchange { get; set; }
    public Dictionary<string, string> CurrencyTickerSymbols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Comments { get; set; } = "";
    public Dictionary<string, string> ProviderSymbols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> CurrencyProviderSymbols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ChartAnalysis? Analysis { get; set; }
}

public sealed class ChartAnalysis
{
    public string Status { get; set; } = "not-requested";
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? SourceImageHash { get; set; }
    public decimal? SuggestedUpperLine { get; set; }
    public decimal? SuggestedLowerLine { get; set; }
    public decimal? Confidence { get; set; }
    public string? Explanation { get; set; }
    public DateTimeOffset? AnalyzedAt { get; set; }
    public bool? AcceptedByUser { get; set; }
}

public sealed class ChartCatalog
{
    public int SchemaVersion { get; set; } = 2;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<RiskRewardChart> Charts { get; set; } = [];
}

public sealed class ChartDraft
{
    public string TickerSymbol { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string ImageHash { get; set; } = "";
    public DateTimeOffset SourceModifiedAt { get; set; }
    public decimal? UpperLine { get; set; }
    public decimal? LowerLine { get; set; }
    public string Currency { get; set; } = "";
    public string? Exchange { get; set; }
    public Dictionary<string, string> CurrencyTickerSymbols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Comments { get; set; } = "";
    public Dictionary<string, string> ProviderSymbols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> CurrencyProviderSymbols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Ready { get; set; }
    public bool Skipped { get; set; }
    public bool ManualReview { get; set; }
    public ChartAnalysis? Analysis { get; set; }
    public string? EditedImagePath { get; set; }
    public string? EditedImageHash { get; set; }
    public bool UseEditedImage { get; set; }
}

public sealed class PublicationRecord
{
    public DateTimeOffset PublishedAt { get; set; }
    public DeploymentTarget Target { get; set; }
    public List<string> Tickers { get; set; } = [];
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
}

public sealed class ApplicationState
{
    public Dictionary<string, RiskRewardChart> Charts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ChartDraft> Drafts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PublicationRecord> Publications { get; set; } = [];
}

public sealed record Quote(string Ticker, decimal Price, DateTimeOffset QuotedAt, string Provider, bool IsStale = false, string? Error = null, string Currency = "USD");

public sealed class PriceCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, Quote> Quotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record HistoricalPricePoint(DateTimeOffset Timestamp, decimal Price);

public sealed class HistoricalPriceFile
{
    public int SchemaVersion { get; set; } = 1;
    public string Symbol { get; set; } = "";
    public string Currency { get; set; } = "";
    public string? Exchange { get; set; }
    public int IntervalMinutes { get; set; } = 15;
    public string Timezone { get; set; } = "UTC";
    public List<HistoricalPricePoint> Points { get; set; } = [];
}

public sealed class HistoryManifestEntry
{
    public string Currency { get; set; } = "";
    public string? Exchange { get; set; }
    public DateTimeOffset First { get; set; }
    public DateTimeOffset Last { get; set; }
    public List<string> Files { get; set; } = [];
}

public static class AllocationCalculator
{
    public static decimal Calculate(decimal currentPrice, decimal lowerLine, decimal upperLine)
    {
        if (currentPrice <= 0 || lowerLine <= 0 || upperLine <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentPrice), "Prices and chart boundaries must be positive.");
        if (lowerLine >= upperLine)
            throw new ArgumentException("The lower line must be below the upper line.");

        var raw = (Math.Log((double)upperLine) - Math.Log((double)currentPrice)) /
                  (Math.Log((double)upperLine) - Math.Log((double)lowerLine)) * 10d;
        return Math.Round((decimal)Math.Clamp(raw, 0d, 10d), 2, MidpointRounding.AwayFromZero);
    }
}

public interface IChartAnalyzer
{
    Task<ChartAnalysis> AnalyzeAsync(string imagePath, CancellationToken cancellationToken = default);
}

public interface IChartImageEditor
{
    Task<string> RemoveVideoBoxAsync(string imagePath, string outputPath, CancellationToken cancellationToken = default);
}

public interface IQuoteProvider
{
    string Name { get; }
    Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(
        IReadOnlyDictionary<string, string> tickerToProviderSymbol,
        CancellationToken cancellationToken = default);
}

public static class MarketMetadata
{
    public static string NormalizeCurrency(string? currency, string ticker)
    {
        var normalized = currency?.Trim().ToUpperInvariant();
        if (normalized is "CAD" or "USD") return normalized;
        return ticker.EndsWith(".V", StringComparison.OrdinalIgnoreCase) || ticker.EndsWith(".TO", StringComparison.OrdinalIgnoreCase)
            ? "CAD"
            : "USD";
    }

    public static string ActiveSymbol(RiskRewardChart chart)
    {
        var currency = NormalizeCurrency(chart.Currency, chart.TickerSymbol);
        return chart.CurrencyTickerSymbols.TryGetValue(currency, out var symbol) && !string.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : chart.TickerSymbol;
    }

    public static string? InferExchange(string? configured, string symbol)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().ToUpperInvariant();
        if (symbol.EndsWith(".V", StringComparison.OrdinalIgnoreCase)) return "TSXV";
        if (symbol.EndsWith(".TO", StringComparison.OrdinalIgnoreCase)) return "TSX";
        return null;
    }
}
