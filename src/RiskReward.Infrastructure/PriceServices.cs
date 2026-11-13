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

    public static bool IsPollingSlot(DateTimeOffset easternTime, int slotMinutes = 15) =>
        easternTime.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
        easternTime.TimeOfDay >= new TimeSpan(9, 30, 0) && easternTime.TimeOfDay <= new TimeSpan(16, 0, 0) &&
        easternTime.Minute % Math.Clamp(slotMinutes, 1, 30) == 0;

    public static string SlotKey(DateTimeOffset easternTime) => easternTime.ToString("yyyy-MM-dd-HH-mm");

    public static bool IsFinalRetryWindow(DateTimeOffset easternTime) =>
        easternTime.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
        easternTime.TimeOfDay >= new TimeSpan(16, 0, 0) && easternTime.TimeOfDay <= new TimeSpan(16, 30, 0);

    public static DateTimeOffset EffectiveQuoteTime(DateTimeOffset utcNow)
    {
        var easternNow = TimeZoneInfo.ConvertTime(utcNow, Eastern);
        var weekday = easternNow.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
        if (weekday && easternNow.TimeOfDay >= new TimeSpan(9, 30, 0) && easternNow.TimeOfDay < new TimeSpan(16, 0, 0))
        {
            // Twelve Data's lightweight price endpoint has no observation timestamp. Use the
            // current scheduler slot so all chunks in one pass share a stable, repeatable time.
            var slotMinute = easternNow.Minute - easternNow.Minute % 15;
            var localSlot = new DateTime(easternNow.Year, easternNow.Month, easternNow.Day,
                easternNow.Hour, slotMinute, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(localSlot, Eastern.GetUtcOffset(localSlot));
        }

        var closeDate = easternNow.Date;
        if (!weekday || easternNow.TimeOfDay < new TimeSpan(16, 0, 0)) closeDate = closeDate.AddDays(-1);
        while (closeDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) closeDate = closeDate.AddDays(-1);
        var localClose = DateTime.SpecifyKind(closeDate.AddHours(16), DateTimeKind.Unspecified);
        return new DateTimeOffset(localClose, Eastern.GetUtcOffset(localClose));
    }

    public static bool IsCurrentMarketSession(DateTimeOffset observation, DateTimeOffset utcNow)
    {
        var expected = TimeZoneInfo.ConvertTime(EffectiveQuoteTime(utcNow), Eastern).Date;
        var observed = TimeZoneInfo.ConvertTime(observation, Eastern).Date;
        return observed == expected;
    }

    /// <summary>
    /// True when a provider's quote belongs to an earlier trading session than the one prices should
    /// reflect now. Free plans return only the previous day's close for OTC listings; that must be
    /// published as old, not as a fresh quote. During the first hour of a session yesterday's last
    /// trade is still accepted, because thinly traded listings may not have traded yet.
    /// </summary>
    public static bool IsLaggingQuote(DateTimeOffset observation, DateTimeOffset utcNow, bool allowOpeningGrace = true)
    {
        var expected = TimeZoneInfo.ConvertTime(EffectiveQuoteTime(utcNow), Eastern).Date;
        var observed = TimeZoneInfo.ConvertTime(observation, Eastern).Date;
        if (observed >= expected) return false;
        if (!allowOpeningGrace) return true;

        var easternNow = TimeZoneInfo.ConvertTime(utcNow, Eastern);
        var weekday = easternNow.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
        var firstHour = weekday && easternNow.TimeOfDay >= new TimeSpan(9, 30, 0) && easternNow.TimeOfDay < new TimeSpan(10, 30, 0);
        if (!firstHour) return true;

        var priorSession = expected.AddDays(-1);
        while (priorSession.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) priorSession = priorSession.AddDays(-1);
        return observed < priorSession;
    }

    /// <summary>
    /// Finnhub stamps end-of-day OTC quotes with midnight UTC of the session date, which reads as the
    /// previous evening in Eastern time. Treat a midnight-UTC stamp as that date's 4:00 PM Eastern close.
    /// </summary>
    public static DateTimeOffset NormalizeDateOnlyTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        if (utc.TimeOfDay != TimeSpan.Zero) return timestamp;
        var localClose = DateTime.SpecifyKind(utc.Date.AddHours(16), DateTimeKind.Unspecified);
        return new DateTimeOffset(localClose, Eastern.GetUtcOffset(localClose));
    }

    public static DateTimeOffset ToEastern(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Eastern);
}

/// <summary>
/// Spreads a small daily EODHD call allowance across the whole trading session. Without it the
/// free plan's 20 calls were all spent by 11:45 AM Eastern and Canadian listings stopped updating
/// for the rest of the day.
/// </summary>
public static class EodhdBudget
{
    private const int SessionSlots = 27; // 9:30 AM through 4:00 PM Eastern, every 15 minutes.

    public static bool IsUnlimited(int dailyCallLimit, int symbolsPerCycle) =>
        dailyCallLimit <= 0 || dailyCallLimit / Math.Max(1, symbolsPerCycle) - 1 >= SessionSlots;

    public static bool AllowsCycle(DateTimeOffset utcNow, int symbolsPerCycle, int dailyCallLimit)
    {
        if (IsUnlimited(dailyCallLimit, symbolsPerCycle)) return true;
        var easternNow = MarketSchedule.ToEastern(utcNow);
        var weekday = easternNow.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
        var open = new TimeSpan(9, 30, 0);
        // Startup refreshes and final-close retries outside the session are not rationed.
        if (!weekday || easternNow.TimeOfDay < open || easternNow.TimeOfDay > new TimeSpan(16, 0, 0)) return true;

        // One cycle is held back so a service restart does not cost the closing quote.
        var cycles = Math.Max(1, dailyCallLimit / Math.Max(1, symbolsPerCycle) - 1);
        var minutesOpen = (int)(easternNow.TimeOfDay - open).TotalMinutes;
        // With a cycle shorter than 15 minutes, only the first cycle of each quarter hour may spend calls.
        if (minutesOpen % 15 >= 5) return false;
        var slot = Math.Clamp(minutesOpen / 15, 0, SessionSlots - 1);
        // Evenly spaced slots that always include the 4:00 PM close.
        return (slot + 1) * cycles / SessionSlots > slot * cycles / SessionSlots;
    }
}

/// <param name="FreshCount">Quotes a provider returned for the current trading session.</param>
/// <param name="LaggingCount">Quotes a provider returned that are from an earlier session.</param>
public sealed record PriceUpdateResult(int FreshCount, int TotalCount, DeploymentTarget Target, int LaggingCount = 0)
{
    /// <summary>Every chart got a provider answer, so repeating the cycle would not help.</summary>
    public bool AllAnswered => TotalCount > 0 && FreshCount + LaggingCount == TotalCount;
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
    private readonly PriceHistoryStore history;
    private readonly CurrencyConversionService currencyConversion;
    private readonly int eodhdDailyCallLimit;
    // Listings for which Finnhub answered with an end-of-day price today (US OTC). Relearned each day,
    // so a listing that moves to an exchange goes back to Finnhub on its own.
    private readonly HashSet<string> endOfDayOnlyAtFinnhub = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly learnedOn;

    public PriceUpdateService(IEnumerable<IQuoteProvider> providers, IOptions<RiskRewardOptions> options, StateStore stateStore, ILogger<PriceUpdateService> logger, ProviderRunCsvLogger providerRuns, PriceHistoryStore history, CurrencyConversionService currencyConversion, IOptions<ProviderOptions> providerOptions)
    {
        this.providers = providers.ToList();
        this.options = options.Value;
        eodhdDailyCallLimit = providerOptions.Value.EodhdDailyCallLimit;
        this.stateStore = stateStore;
        this.logger = logger;
        this.providerRuns = providerRuns;
        this.history = history;
        this.currencyConversion = currencyConversion;
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

        // Each listing is routed to the provider that has the freshest data for it, with no per-symbol
        // configuration, so a newly published chart is handled from its first cycle:
        //  - Finnhub (free) is near real time for exchange-listed US stocks, so it is asked first.
        //  - EODHD (15-20 minutes delayed) is the only source with same-day quotes for Canadian listings
        //    and for US OTC listings, where Finnhub returns just an end-of-day price. Canadian listings are
        //    recognised by currency; OTC listings are learned from Finnhub's end-of-day answer and then go to
        //    EODHD first for the rest of the day.
        //  - Twelve Data (8 requests a minute on the free plan) is only a last fallback.
        var finnhub = FindProvider("finnhub");
        var twelveData = FindProvider("twelveData");
        var eodhd = FindProvider("eodhd");
        var now = DateTimeOffset.UtcNow;
        var unlimitedEodhd = eodhd is not null && EodhdBudget.IsUnlimited(eodhdDailyCallLimit, catalog.Charts.Count);
        var today = DateOnly.FromDateTime(MarketSchedule.ToEastern(now).Date);
        if (today != learnedOn) { endOfDayOnlyAtFinnhub.Clear(); learnedOn = today; }

        var quotes = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        var rationed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var confirmedLatest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<RiskRewardChart> Missing() => catalog.Charts.Where(chart => !quotes.ContainsKey(chart.TickerSymbol)).ToList();
        // A quote from an earlier session is not an answer: the next provider is asked for a newer one.
        List<RiskRewardChart> Lagging(bool allowOpeningGrace) => catalog.Charts.Where(chart =>
            !confirmedLatest.Contains(chart.TickerSymbol) && quotes.TryGetValue(chart.TickerSymbol, out var held) &&
            MarketSchedule.IsLaggingQuote(held.QuotedAt, now, allowOpeningGrace)).ToList();

        async Task AskAsync(IQuoteProvider provider, List<RiskRewardChart> wanted)
        {
            if (wanted.Count == 0) return;
            var providerCatalog = new ChartCatalog { Charts = wanted };
            foreach (var pair in await GetQuotesSafelyAsync(provider, BuildSymbols(providerCatalog, provider.Name), cancellationToken))
            {
                // EODHD has same-day data, so its answer is the listing's latest trade even when that trade
                // was in an earlier session (thinly traded OTC listings). It also wins a timestamp tie.
                if (IsEodhd(provider)) confirmedLatest.Add(pair.Key);
                if (pair.Value.IsEndOfDayOnly && !IsEodhd(provider)) endOfDayOnlyAtFinnhub.Add(pair.Key);
                if (!quotes.TryGetValue(pair.Key, out var held) || pair.Value.QuotedAt > held.QuotedAt ||
                    (IsEodhd(provider) && pair.Value.QuotedAt == held.QuotedAt))
                    quotes[pair.Key] = pair.Value;
            }
        }

        var eodhdFirst = unlimitedEodhd
            ? catalog.Charts.Where(chart => IsCanadianListing(chart) || endOfDayOnlyAtFinnhub.Contains(chart.TickerSymbol)).ToList()
            : [];
        if (unlimitedEodhd) await AskAsync(eodhd!, eodhdFirst);
        if (finnhub is not null) await AskAsync(finnhub, Missing());
        if (unlimitedEodhd)
        {
            // From the opening bell: EODHD may already have today's first trade.
            var wanted = Missing().Concat(Lagging(allowOpeningGrace: false)).ToList();
            if (wanted.Count > 0)
                logger.LogInformation("Finnhub had no current-session quote for {Count} symbols; asking EODHD.", wanted.Count);
            await AskAsync(eodhd!, wanted);
            var stillMissing = Missing();
            if (twelveData is not null && stillMissing.Count > 0)
            {
                logger.LogWarning("Quote providers still missed {Count} symbols; trying {Provider}.", stillMissing.Count, twelveData.Name);
                await AskAsync(twelveData, stillMissing);
            }
        }
        else
        {
            // Free EODHD plan: Twelve Data is asked before the rationed EODHD calls are spent.
            if (twelveData is not null) await AskAsync(twelveData, Missing().Concat(Lagging(allowOpeningGrace: true)).ToList());
            var missing = Missing();
            if (eodhd is not null && missing.Count > 0)
            {
                if (EodhdBudget.AllowsCycle(now, missing.Count, eodhdDailyCallLimit)) await AskAsync(eodhd, missing);
                else
                {
                    foreach (var chart in missing) rationed.Add(chart.TickerSymbol);
                    logger.LogInformation("EODHD was not called for {Count} symbols this cycle; its {Limit} daily calls are spread across the trading session.", missing.Count, eodhdDailyCallLimit);
                }
            }
        }

        // The derived pass must not spend rationed EODHD calls on USD listings.
        var derivedProviders = new[] { finnhub, twelveData, unlimitedEodhd ? eodhd : null }
            .Where(provider => provider is not null).Select(provider => provider!).ToList();
        await AddDerivedCadFallbacksAsync(catalog, quotes, derivedProviders, cancellationToken);

        var answeredQuotes = new Dictionary<string, Quote>(quotes, StringComparer.OrdinalIgnoreCase);
        // Stale means no same-day source answered. A listing whose latest trade, as confirmed by EODHD
        // this cycle, was in an earlier session simply has not traded yet today.
        var laggingTickers = quotes.Where(pair => !confirmedLatest.Contains(pair.Key) && MarketSchedule.IsLaggingQuote(pair.Value.QuotedAt, now))
            .Select(pair => pair.Key).OrderBy(ticker => ticker, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var ticker in laggingTickers)
            quotes[ticker] = quotes[ticker] with { IsStale = true, Error = "The data providers' latest quote is from an earlier trading session." };
        var freshCount = answeredQuotes.Count - laggingTickers.Count;
        if (laggingTickers.Count > 0)
            logger.LogWarning("{Count} symbols only have a quote from an earlier trading session and were published as stale: {Tickers}.", laggingTickers.Count, string.Join(", ", laggingTickers));

        var previous = await ReadPricesAsync(target, cancellationToken);
        foreach (var chart in catalog.Charts)
        {
            if (quotes.ContainsKey(chart.TickerSymbol)) continue;
            if (!previous.Quotes.TryGetValue(chart.TickerSymbol, out var old) || !MarketMetadata.QuoteMatchesChartCurrency(chart, old)) continue;
            // Between rationed EODHD cycles the last quote is still this session's and is kept as it was.
            var keptBetweenCycles = rationed.Contains(chart.TickerSymbol) && !old.IsStale && !MarketSchedule.IsLaggingQuote(old.QuotedAt, now);
            quotes[chart.TickerSymbol] = keptBetweenCycles ? old : old with
            {
                IsStale = true,
                Error = rationed.Contains(chart.TickerSymbol) ? "No current-session quote yet; the next scheduled provider call will refresh it." : "All quote providers failed."
            };
        }
        var exchangeRates = await GetPublishedExchangeRatesAsync(previous, cancellationToken);
        await WritePricesAsync(target, new PriceCatalog { GeneratedAt = DateTimeOffset.UtcNow, Quotes = quotes, ExchangeRates = exchangeRates }, cancellationToken);
        try { await history.UpdateAsync(target, catalog, answeredQuotes, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogError(ex, "Current prices were published, but the history update failed and will be retried on a later run."); }
        logger.LogInformation("Published {Count}/{Total} prices to {Target}, including {FreshCount} fresh quotes and {LaggingCount} from an earlier session; {EodhdFirst} symbols were routed to EODHD first.", quotes.Count, catalog.Charts.Count, target, freshCount, laggingTickers.Count, eodhdFirst.Count);
        return new PriceUpdateResult(freshCount, catalog.Charts.Count, target, laggingTickers.Count);
    }

    private static bool IsEodhd(IQuoteProvider provider) => provider.Name.Equals("eodhd", StringComparison.OrdinalIgnoreCase);

    private IQuoteProvider? FindProvider(string name) =>
        providers.FirstOrDefault(provider => provider.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsCanadianListing(RiskRewardChart chart) =>
        MarketMetadata.NormalizeCurrency(chart.Currency, chart.TickerSymbol) == "CAD";

    private async Task<Dictionary<string, PublishedExchangeRate>> GetPublishedExchangeRatesAsync(
        PriceCatalog previous,
        CancellationToken cancellationToken)
    {
        try
        {
            var rate = await currencyConversion.GetRateAsync("USD", "CAD", cancellationToken);
            return new Dictionary<string, PublishedExchangeRate>(StringComparer.OrdinalIgnoreCase)
            {
                ["USDCAD"] = new(rate.From, rate.To, rate.Rate, rate.EffectiveDate, rate.Provider)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning(ex, "The USD/CAD portfolio conversion rate could not be refreshed.");
            if (previous.ExchangeRates.TryGetValue("USDCAD", out var old))
                return new Dictionary<string, PublishedExchangeRate>(StringComparer.OrdinalIgnoreCase)
                {
                    ["USDCAD"] = old with { IsStale = true }
                };
            return new Dictionary<string, PublishedExchangeRate>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task AddDerivedCadFallbacksAsync(
        ChartCatalog catalog,
        Dictionary<string, Quote> quotes,
        IReadOnlyList<IQuoteProvider> providerOrder,
        CancellationToken cancellationToken)
    {
        var candidates = catalog.Charts.Where(chart =>
            !quotes.ContainsKey(chart.TickerSymbol) &&
            MarketMetadata.NormalizeCurrency(chart.Currency, chart.TickerSymbol) == "CAD" &&
            chart.CurrencyTickerSymbols.TryGetValue("USD", out var usdSymbol) && !string.IsNullOrWhiteSpace(usdSymbol)).ToList();
        if (candidates.Count == 0) return;

        CurrencyRate rate;
        try { rate = await currencyConversion.GetRateAsync("USD", "CAD", cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning(ex, "USD-listed fallback quotes could not be converted to CAD.");
            return;
        }

        foreach (var provider in providerOrder)
        {
            var unresolved = candidates.Where(chart => !quotes.ContainsKey(chart.TickerSymbol)).ToList();
            if (unresolved.Count == 0) break;
            var fallbackCatalog = new ChartCatalog { Charts = unresolved };
            var symbols = BuildSymbols(fallbackCatalog, provider.Name, "USD");
            var fallbackQuotes = await GetQuotesSafelyAsync(provider, symbols, cancellationToken);
            foreach (var pair in fallbackQuotes)
            {
                var source = pair.Value;
                if (!source.TimestampIsProviderSupplied || !MarketSchedule.IsCurrentMarketSession(source.QuotedAt, DateTimeOffset.UtcNow))
                {
                    logger.LogWarning("Rejected stale or timestamp-free derived fallback for {Ticker} from {Provider} at {Timestamp:O}.",
                        pair.Key, provider.Name, source.QuotedAt);
                    continue;
                }
                var sourceSymbol = symbols[pair.Key].Trim().ToUpperInvariant();
                quotes[pair.Key] = source with
                {
                    Ticker = pair.Key,
                    Price = decimal.Round(source.Price * rate.Rate, 6, MidpointRounding.AwayFromZero),
                    Provider = $"{source.Provider} + {rate.Provider}",
                    Currency = "CAD",
                    IsDerived = true,
                    SourceSymbol = sourceSymbol,
                    ConversionRate = rate.Rate,
                    ConversionProvider = rate.Provider,
                    PreviousClose = source.PreviousClose is { } previousClose
                        ? decimal.Round(previousClose * rate.Rate, 6, MidpointRounding.AwayFromZero)
                        : null,
                    DailyChange = source.DailyChange is { } dailyChange
                        ? decimal.Round(dailyChange * rate.Rate, 6, MidpointRounding.AwayFromZero)
                        : null,
                    DailyChangePercent = source.DailyChangePercent
                };
                logger.LogInformation("Derived CAD quote for {Ticker} from {SourceSymbol}: USD {UsdPrice} x {Rate} = CAD {CadPrice}.",
                    pair.Key, sourceSymbol, source.Price, rate.Rate, quotes[pair.Key].Price);
            }
        }
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

    private static Dictionary<string, string> BuildSymbols(ChartCatalog catalog, string provider, string? currencyOverride = null) => catalog.Charts.ToDictionary(
        chart => chart.TickerSymbol,
        chart =>
        {
            var currency = currencyOverride ?? MarketMetadata.NormalizeCurrency(chart.Currency, chart.TickerSymbol);
            if (chart.CurrencyProviderSymbols.TryGetValue(currency, out var currencySymbols) &&
                currencySymbols.TryGetValue(provider, out var currencyMapped) && !string.IsNullOrWhiteSpace(currencyMapped)) return currencyMapped;
            if (chart.ProviderSymbols.TryGetValue(provider, out var mapped) && !string.IsNullOrWhiteSpace(mapped)) return mapped;
            if (chart.CurrencyTickerSymbols.TryGetValue(currency, out var currencyTicker) && !string.IsNullOrWhiteSpace(currencyTicker))
                return currencyTicker;
            return MarketMetadata.ActiveSymbol(chart);
        },
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
