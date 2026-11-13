using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace HSPI_RiskRewardStatus
{
    internal sealed class PriceCatalog
    {
        [JsonProperty("generatedAt")]
        public DateTimeOffset GeneratedAt { get; set; }

        [JsonProperty("quotes")]
        public Dictionary<string, PriceQuote> Quotes { get; set; } = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase);

        // Published by the price service only on a day the US market is closed for a holiday.
        [JsonProperty("marketHoliday")]
        public MarketHolidayNotice MarketHoliday { get; set; }
    }

    internal sealed class MarketHolidayNotice
    {
        [JsonProperty("date")]
        public string Date { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    internal sealed class PriceQuote
    {
        [JsonProperty("quotedAt")]
        public DateTimeOffset QuotedAt { get; set; }

        [JsonProperty("isStale")]
        public bool IsStale { get; set; }
    }

    internal sealed class SiteStatus
    {
        public int Value { get; set; }
        public string Text { get; set; }
    }

    internal static class MarketStatusCalculator
    {
        private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        public static SiteStatus Calculate(PriceCatalog catalog, DateTimeOffset now)
        {
            var quotes = catalog?.Quotes?.Values.ToList() ?? new List<PriceQuote>();
            if (quotes.Count == 0) return Status(0, "Prices unavailable");

            var explicitlyStale = quotes.All(quote => quote.IsStale);
            var easternNow = TimeZoneInfo.ConvertTime(now, Eastern);
            var weekday = easternNow.DayOfWeek != DayOfWeek.Saturday && easternNow.DayOfWeek != DayOfWeek.Sunday;
            // A holiday is a closed day: no new prices are expected, only the last trading day's close.
            var holiday = catalog.MarketHoliday;
            var holidayToday = holiday != null && holiday.Date != null && holiday.Date.Length >= 10 &&
                               holiday.Date.Substring(0, 10) == easternNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var marketOpen = weekday && !holidayToday && easternNow.TimeOfDay >= new TimeSpan(9, 30, 0) && easternNow.TimeOfDay < new TimeSpan(16, 0, 0);
            bool stale;
            if (marketOpen)
            {
                var newest = quotes.Max(quote => quote.QuotedAt);
                stale = explicitlyStale || newest == default(DateTimeOffset) || now - newest > TimeSpan.FromMinutes(30);
            }
            else
            {
                stale = explicitlyStale || !HasRequiredCloseUpdate(catalog.GeneratedAt, easternNow);
            }

            if (stale) return Status(1, "Prices may be stale");

            // The price service marks a quote stale when no provider has a current-session price for it.
            // Some stale symbols is still a warning, even though the rest of the site is current.
            var staleTickers = catalog.Quotes.Where(pair => pair.Value != null && pair.Value.IsStale)
                .Select(pair => pair.Key).OrderBy(ticker => ticker, StringComparer.OrdinalIgnoreCase).ToList();
            if (staleTickers.Count > 0)
            {
                var names = string.Join(", ", staleTickers.Take(4)) + (staleTickers.Count > 4 ? ", +" + (staleTickers.Count - 4) : "");
                return Status(1, staleTickers.Count + " of " + quotes.Count + " prices stale: " + names);
            }
            if (marketOpen) return Status(2, "Prices current");
            return Status(3, holidayToday && !string.IsNullOrWhiteSpace(holiday.Name) ? "Market closed: " + holiday.Name.Trim() : "Market closed");
        }

        private static bool HasRequiredCloseUpdate(DateTimeOffset generatedAt, DateTimeOffset easternNow)
        {
            if (generatedAt == default(DateTimeOffset)) return false;
            var requiredDate = MostRecentRequiredCloseDate(easternNow);
            var generatedEastern = TimeZoneInfo.ConvertTime(generatedAt, Eastern);
            return generatedEastern.Date > requiredDate ||
                   (generatedEastern.Date == requiredDate && generatedEastern.TimeOfDay >= new TimeSpan(16, 0, 0));
        }

        private static DateTime MostRecentRequiredCloseDate(DateTimeOffset easternNow)
        {
            var weekday = easternNow.DayOfWeek != DayOfWeek.Saturday && easternNow.DayOfWeek != DayOfWeek.Sunday;
            if (weekday && easternNow.TimeOfDay >= new TimeSpan(16, 0, 0)) return easternNow.Date;
            var date = easternNow.Date.AddDays(-1);
            while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(-1);
            return date;
        }

        private static SiteStatus Status(int value, string text) => new SiteStatus { Value = value, Text = text };
    }
}
