using System.Globalization;
using System.Text.Json;

namespace RiskReward.Infrastructure;

public sealed record CurrencyRate(string From, string To, decimal Rate, DateOnly EffectiveDate, string Provider);

public sealed class CurrencyConversionService
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CurrencyRate? cachedUsdCad;
    private DateTimeOffset cacheExpiresAt;

    public CurrencyConversionService(HttpClient http)
    {
        this.http = http;
        this.http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<CurrencyRate> GetRateAsync(string from, string to, CancellationToken cancellationToken = default)
    {
        from = Normalize(from);
        to = Normalize(to);
        if (from == to) return new CurrencyRate(from, to, 1m, DateOnly.FromDateTime(DateTime.UtcNow), "Identity");
        if (from is not ("USD" or "CAD") || to is not ("USD" or "CAD"))
            throw new ArgumentException("Only USD and CAD conversions are supported.");

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cachedUsdCad is null || DateTimeOffset.UtcNow >= cacheExpiresAt)
            {
                using var response = await http.GetAsync("https://www.bankofcanada.ca/valet/observations/FXUSDCAD/json?recent=1", cancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var observation = document.RootElement.GetProperty("observations").EnumerateArray().LastOrDefault();
                if (observation.ValueKind != JsonValueKind.Object ||
                    !observation.TryGetProperty("d", out var dateNode) ||
                    !DateOnly.TryParse(dateNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveDate) ||
                    !observation.TryGetProperty("FXUSDCAD", out var rateNode) ||
                    !rateNode.TryGetProperty("v", out var valueNode) ||
                    !decimal.TryParse(valueNode.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) || rate <= 0)
                    throw new InvalidOperationException("The Bank of Canada did not return a usable USD/CAD rate.");
                cachedUsdCad = new CurrencyRate("USD", "CAD", rate, effectiveDate, "Bank of Canada Valet");
                cacheExpiresAt = DateTimeOffset.UtcNow.AddHours(12);
            }

            return from == "USD"
                ? cachedUsdCad
                : new CurrencyRate("CAD", "USD", decimal.Round(1m / cachedUsdCad.Rate, 10), cachedUsdCad.EffectiveDate, cachedUsdCad.Provider);
        }
        finally { gate.Release(); }
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
