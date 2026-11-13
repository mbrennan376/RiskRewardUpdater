using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

/// <summary>Full-day market closures by exchange, plus the names of the US ones.</summary>
public sealed class MarketCalendar
{
    public static readonly MarketCalendar Empty = new(new Dictionary<string, List<MarketHoliday>>());

    private readonly Dictionary<DateOnly, string> usNames;

    public MarketCalendar(IReadOnlyDictionary<string, List<MarketHoliday>> holidaysByExchange)
    {
        HolidaysByExchange = holidaysByExchange;
        var us = Closed(holidaysByExchange, "US");
        UsClosed = us.Keys.ToHashSet();
        usNames = us;
        // TSX and TSX Venture keep the same calendar; either answer is enough.
        CanadaClosed = Closed(holidaysByExchange, "TO").Keys.Concat(Closed(holidaysByExchange, "V").Keys).ToHashSet();
    }

    public IReadOnlyDictionary<string, List<MarketHoliday>> HolidaysByExchange { get; }
    public IReadOnlySet<DateOnly> UsClosed { get; }
    public IReadOnlySet<DateOnly> CanadaClosed { get; }

    public MarketHolidayNotice? UsHolidayOn(DateOnly date) =>
        usNames.TryGetValue(date, out var name) ? new MarketHolidayNotice(date, name) : null;

    // A day with shortened trading hours is still a trading day.
    private static Dictionary<DateOnly, string> Closed(IReadOnlyDictionary<string, List<MarketHoliday>> source, string exchange) =>
        source.TryGetValue(exchange, out var list)
            ? list.Where(holiday => string.IsNullOrWhiteSpace(holiday.TradingHours))
                .GroupBy(holiday => holiday.Date).ToDictionary(group => group.Key, group => group.First().Name)
            : [];
}

public sealed record MarketHoliday(DateOnly Date, string Name, string? TradingHours);

/// <summary>
/// Loads market holidays from Finnhub's free <c>/stock/market-holiday</c> endpoint once a day and keeps
/// the last good answer in the state folder, so a failed call or a restart never loses the calendar.
/// Without a calendar every weekday is treated as a trading day, which is the behaviour before this existed.
/// </summary>
public sealed class MarketCalendarService
{
    private static readonly string[] Exchanges = ["US", "TO", "V"];
    private readonly HttpClient http;
    private readonly ProviderOptions options;
    private readonly ILogger<MarketCalendarService> logger;
    private readonly string cachePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MarketCalendar calendar = MarketCalendar.Empty;
    private Dictionary<string, List<MarketHoliday>> byExchange = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly loadedOn;
    private DateTimeOffset retryAfter;
    private bool cacheLoaded;

    public MarketCalendarService(HttpClient http, IOptions<ProviderOptions> options, IOptions<RiskRewardOptions> riskReward, ILogger<MarketCalendarService> logger)
    {
        this.http = http;
        this.options = options.Value;
        this.logger = logger;
        this.http.Timeout = TimeSpan.FromSeconds(Math.Max(5, this.options.RequestTimeoutSeconds));
        cachePath = Path.Combine(riskReward.Value.StateFolder, "market-holidays.json");
    }

    public async Task<MarketCalendar> GetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!cacheLoaded) { cacheLoaded = true; LoadCache(); }
            var today = DateOnly.FromDateTime(MarketSchedule.ToEastern(DateTimeOffset.UtcNow).Date);
            var nowUtc = DateTimeOffset.UtcNow;
            if (loadedOn == today || nowUtc < retryAfter || string.IsNullOrWhiteSpace(options.FinnhubApiKey)) return calendar;

            var changed = false;
            var usLoaded = false;
            var canadaLoaded = false;
            foreach (var exchange in Exchanges)
            {
                try
                {
                    var holidays = await FetchAsync(exchange, cancellationToken);
                    // An empty answer never replaces a calendar that was already loaded.
                    if (holidays.Count == 0) { logger.LogWarning("Finnhub returned no market holidays for {Exchange}; the saved calendar is kept.", exchange); continue; }
                    byExchange[exchange] = holidays;
                    changed = true;
                    if (exchange == "US") usLoaded = true; else canadaLoaded = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
                {
                    logger.LogWarning("Market holidays for {Exchange} could not be loaded from Finnhub ({Reason}); the saved calendar is kept.", exchange, ex.Message);
                }
            }
            // Loaded once a day. A failed US load is tried again in half an hour; if only the Canadian
            // calendars are missing, in six hours, so their holidays are not left unknown until tomorrow.
            if (usLoaded && canadaLoaded) loadedOn = today;
            else retryAfter = nowUtc.Add(usLoaded ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(30));
            if (changed)
            {
                calendar = new MarketCalendar(byExchange);
                SaveCache();
                var next = calendar.UsClosed.Where(date => date >= today).OrderBy(date => date).FirstOrDefault();
                logger.LogInformation("Market holiday calendar loaded: {UsCount} US and {CanadaCount} Canadian full-day closures; next US closure {Next}.",
                    calendar.UsClosed.Count, calendar.CanadaClosed.Count, next == default ? "none listed" : calendar.UsHolidayOn(next)!.Name + " on " + next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            return calendar;
        }
        finally { gate.Release(); }
    }

    private async Task<List<MarketHoliday>> FetchAsync(string exchange, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            $"https://finnhub.io/api/v1/stock/market-holiday?exchange={Uri.EscapeDataString(exchange)}&token={Uri.EscapeDataString(options.FinnhubApiKey!)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>Reads Finnhub's <c>{ "data": [ { "eventName", "atDate", "tradingHour" } ] }</c> answer.</summary>
    public static List<MarketHoliday> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<MarketHoliday>();
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("atDate", out var dateNode) || dateNode.ValueKind != JsonValueKind.String ||
                !DateOnly.TryParseExact(dateNode.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var name = item.TryGetProperty("eventName", out var nameNode) && nameNode.ValueKind == JsonValueKind.String ? nameNode.GetString() : null;
            var hours = item.TryGetProperty("tradingHour", out var hoursNode) && hoursNode.ValueKind == JsonValueKind.String ? hoursNode.GetString() : null;
            result.Add(new MarketHoliday(date, string.IsNullOrWhiteSpace(name) ? "Market holiday" : name!.Trim(), string.IsNullOrWhiteSpace(hours) ? null : hours!.Trim()));
        }
        return result;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, List<MarketHoliday>>>(File.ReadAllText(cachePath), JsonDefaults.Options);
            if (saved is null) return;
            byExchange = new Dictionary<string, List<MarketHoliday>>(saved, StringComparer.OrdinalIgnoreCase);
            calendar = new MarketCalendar(byExchange);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("The saved market holiday calendar could not be read ({Reason}).", ex.Message);
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(byExchange, JsonDefaults.Options));
            File.Move(temporary, cachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The market holiday calendar could not be saved ({Reason}).", ex.Message);
        }
    }
}
