using System;
using System.Collections.Generic;
using Xunit;

namespace HSPI_RiskRewardStatus.Tests
{
    public sealed class MarketStatusCalculatorTests
    {
        [Fact]
        public void NoQuotesAreUnavailable()
        {
            AssertStatus(new PriceCatalog(), Utc(2026, 9, 23, 15, 0), 0, "Prices unavailable");
        }

        [Fact]
        public void FreshQuoteDuringMarketHoursIsCurrent()
        {
            var now = Utc(2026, 9, 23, 15, 0);
            AssertStatus(Catalog(now, now.AddMinutes(-5)), now, 2, "Prices current");
        }

        [Fact]
        public void OldQuoteDuringMarketHoursIsStale()
        {
            var now = Utc(2026, 9, 23, 15, 0);
            AssertStatus(Catalog(now, now.AddMinutes(-31)), now, 1, "Prices may be stale");
        }

        [Fact]
        public void UpdateAtFourPmMakesClosedMarketCurrent()
        {
            var now = Utc(2026, 9, 23, 20, 40);
            AssertStatus(Catalog(Utc(2026, 9, 23, 20, 0), Utc(2026, 9, 23, 20, 0)), now, 3, "Market closed");
        }

        [Fact]
        public void UpdateBeforeFourPmMakesClosedMarketStale()
        {
            var now = Utc(2026, 9, 23, 20, 40);
            AssertStatus(Catalog(Utc(2026, 9, 23, 19, 59), Utc(2026, 9, 23, 19, 59)), now, 1, "Prices may be stale");
        }

        [Fact]
        public void WeekendUsesFridayClose()
        {
            var now = Utc(2026, 9, 26, 16, 0);
            AssertStatus(Catalog(Utc(2026, 9, 25, 20, 5), Utc(2026, 9, 25, 20, 5)), now, 3, "Market closed");
        }

        [Fact]
        public void SomeStaleQuotesDuringMarketHoursWarnAndNameThem()
        {
            var now = Utc(2026, 11, 11, 20, 15);
            var catalog = Catalog(now, now.AddMinutes(-1));
            catalog.Quotes["ITMSF"] = new PriceQuote { QuotedAt = Utc(2026, 11, 10, 21, 0), IsStale = true };
            catalog.Quotes["DBOXF"] = new PriceQuote { QuotedAt = Utc(2026, 11, 10, 21, 0), IsStale = true };
            AssertStatus(catalog, now, 1, "2 of 3 prices stale: DBOXF, ITMSF");
        }

        [Fact]
        public void SomeStaleQuotesAfterTheCloseStillWarn()
        {
            var now = Utc(2026, 11, 11, 21, 40);
            var catalog = Catalog(Utc(2026, 11, 11, 21, 0), Utc(2026, 11, 11, 21, 0));
            catalog.Quotes["DBOXF"] = new PriceQuote { QuotedAt = Utc(2026, 11, 10, 21, 0), IsStale = true };
            AssertStatus(catalog, now, 1, "1 of 2 prices stale: DBOXF");
        }

        [Fact]
        public void HolidayIsAClosedDayNotStalePrices()
        {
            // Thanksgiving 2026 at 11:00 AM Eastern: the newest quotes are Wednesday's close.
            var now = Utc(2026, 11, 26, 16, 0);
            var catalog = Catalog(now.AddMinutes(-1), Utc(2026, 11, 25, 21, 0));
            AssertStatus(catalog, now, 1, "Prices may be stale");

            catalog.MarketHoliday = new MarketHolidayNotice { Date = "2026-11-26", Name = "Thanksgiving Day" };
            AssertStatus(catalog, now, 3, "Market closed: Thanksgiving Day");
        }

        [Fact]
        public void YesterdaysHolidayNoticeDoesNotCloseToday()
        {
            var now = Utc(2026, 11, 27, 16, 0);
            var catalog = Catalog(now.AddMinutes(-1), now.AddMinutes(-2));
            catalog.MarketHoliday = new MarketHolidayNotice { Date = "2026-11-26", Name = "Thanksgiving Day" };
            AssertStatus(catalog, now, 2, "Prices current");
        }

        [Fact]
        public void ReadsTheHolidayNoticeFromPricesJson()
        {
            var catalog = Newtonsoft.Json.JsonConvert.DeserializeObject<PriceCatalog>(
                "{\"generatedAt\":\"2026-11-26T15:59:00+00:00\",\"marketHoliday\":{\"date\":\"2026-11-26\",\"name\":\"Thanksgiving Day\"}," +
                "\"quotes\":{\"TEST\":{\"quotedAt\":\"2026-11-25T21:00:00+00:00\",\"isStale\":false}}}");
            AssertStatus(catalog, Utc(2026, 11, 26, 16, 0), 3, "Market closed: Thanksgiving Day");
        }

        [Fact]
        public void LongStaleListIsShortened()
        {
            var now = Utc(2026, 11, 11, 20, 15);
            var catalog = Catalog(now, now.AddMinutes(-1));
            foreach (var ticker in new[] { "F", "E", "D", "C", "B", "A" })
                catalog.Quotes[ticker] = new PriceQuote { QuotedAt = Utc(2026, 11, 10, 21, 0), IsStale = true };
            AssertStatus(catalog, now, 1, "6 of 7 prices stale: A, B, C, D, +2");
        }

        private static PriceCatalog Catalog(DateTimeOffset generatedAt, DateTimeOffset quotedAt)
        {
            return new PriceCatalog
            {
                GeneratedAt = generatedAt,
                Quotes = new Dictionary<string, PriceQuote>
                {
                    ["TEST"] = new PriceQuote { QuotedAt = quotedAt, IsStale = false }
                }
            };
        }

        private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
            new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

        private static void AssertStatus(PriceCatalog catalog, DateTimeOffset now, int value, string text)
        {
            var actual = MarketStatusCalculator.Calculate(catalog, now);
            Assert.Equal(value, actual.Value);
            Assert.Equal(text, actual.Text);
        }
    }
}
