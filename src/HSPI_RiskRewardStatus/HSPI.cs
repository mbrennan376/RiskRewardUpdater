using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HomeSeer.Jui.Views;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Devices;
using Newtonsoft.Json;

namespace HSPI_RiskRewardStatus
{
    public sealed class HSPI : AbstractPlugin
    {
        private const string PricesUrl = "https://riskreward.z13.web.core.windows.net/prices.json";
        private const string CatalogUrl = "https://riskreward.z13.web.core.windows.net/data.json";
        private const string LastUpdatedAddress = "riskreward-last-updated";
        private const string CurrentStatusAddress = "riskreward-current-status";
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
        private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private Timer pollTimer;
        private int pollRunning;
        private int lastUpdatedRef;
        private int currentStatusRef;
        private string lastError;

        public override string Id { get; } = "RiskRewardStatus";
        public override string Name { get; } = "Risk Reward Site Status";
        protected override string SettingsFileName { get; } = "RiskRewardStatus.ini";

        protected override void Initialize()
        {
            lastUpdatedRef = EnsureStatusDevice("Risk/Reward Last Updated", LastUpdatedAddress);
            currentStatusRef = EnsureStatusDevice("Risk/Reward Current Status", CurrentStatusAddress);
            pollTimer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
            Status = PluginStatus.Info("Waiting for the first site poll.");
        }

        private int EnsureStatusDevice(string name, string featureAddress)
        {
            var existing = FindRef(featureAddress);
            if (existing > 0) return existing;

            var feature = FeatureFactory.CreateFeature(Id)
                .WithName(name)
                .WithAddress(featureAddress)
                .WithLocation("Risk/Reward")
                .WithLocation2("Monitoring");
            var device = DeviceFactory.CreateDevice(Id)
                .WithName(name)
                .WithAddress(featureAddress + "-device")
                .WithLocation("Risk/Reward")
                .WithLocation2("Monitoring")
                .WithFeature(feature)
                .PrepareForHs();
            HomeSeerSystem.CreateDevice(device);

            existing = FindRef(featureAddress);
            if (existing <= 0) throw new InvalidOperationException("HomeSeer did not create the " + name + " status feature.");
            return existing;
        }

        private int FindRef(string address)
        {
            Dictionary<int, object> addresses = HomeSeerSystem.GetPropertyByInterface(Id, EProperty.Address);
            return addresses.Where(pair => string.Equals(Convert.ToString(pair.Value), address, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .FirstOrDefault();
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref pollRunning, 1) != 0) return;
            try { PollAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                lastError = ex.Message;
                HomeSeerSystem.UpdateFeatureValueByRef(currentStatusRef, 0);
                HomeSeerSystem.UpdateFeatureValueStringByRef(currentStatusRef, "Prices unavailable");
                Status = PluginStatus.Warning("The live site could not be polled: " + ex.Message);
                Console.WriteLine("Risk/Reward poll failed: " + ex);
            }
            finally { Interlocked.Exchange(ref pollRunning, 0); }
        }

        private async Task PollAsync()
        {
            var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var catalogTask = http.GetStringAsync(CatalogUrl + "?hs=" + cacheBuster);
            var pricesTask = http.GetStringAsync(PricesUrl + "?hs=" + cacheBuster);
            await Task.WhenAll(catalogTask, pricesTask).ConfigureAwait(false);
            var prices = JsonConvert.DeserializeObject<PriceCatalog>(pricesTask.Result);
            if (prices == null) throw new InvalidOperationException("prices.json was empty or invalid.");

            var siteStatus = MarketStatusCalculator.Calculate(prices, DateTimeOffset.UtcNow);
            var updatedText = prices.GeneratedAt == default(DateTimeOffset)
                ? "Unavailable"
                : prices.GeneratedAt.ToLocalTime().ToString("g");
            var updatedValue = prices.GeneratedAt == default(DateTimeOffset) ? 0 : prices.GeneratedAt.ToUnixTimeSeconds();

            HomeSeerSystem.UpdateFeatureValueByRef(lastUpdatedRef, updatedValue);
            HomeSeerSystem.UpdateFeatureValueStringByRef(lastUpdatedRef, updatedText);
            HomeSeerSystem.UpdateFeatureValueByRef(currentStatusRef, siteStatus.Value);
            HomeSeerSystem.UpdateFeatureValueStringByRef(currentStatusRef, siteStatus.Text);
            lastError = null;
            Status = PluginStatus.Ok();
        }

        protected override bool OnSettingChange(string pageId, AbstractView currentView, AbstractView changedView) => true;

        protected override void BeforeReturnStatus()
        {
            if (!string.IsNullOrWhiteSpace(lastError)) Status = PluginStatus.Warning("The last poll failed: " + lastError);
        }

        protected override void OnShutdown()
        {
            pollTimer?.Dispose();
            http.Dispose();
        }
    }
}
