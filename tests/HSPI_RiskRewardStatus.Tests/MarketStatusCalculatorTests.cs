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
