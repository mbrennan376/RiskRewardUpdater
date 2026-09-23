using RiskReward.Infrastructure;

namespace RiskReward.PriceService;

public sealed class Worker(PriceUpdateService updater, ILogger<Worker> logger) : BackgroundService
{
    private readonly TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private string? lastSlot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Risk/Reward price service started in safe local-preview mode unless the shared deployment target says otherwise.");
        while (!stoppingToken.IsCancellationRequested)
        {
            var easternNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, eastern);
            var slot = MarketSchedule.SlotKey(easternNow);
            if (MarketSchedule.IsPollingSlot(easternNow) && slot != lastSlot)
            {
                lastSlot = slot;
                try { await updater.UpdateAsync(stoppingToken); }
                catch (Exception ex) { logger.LogError(ex, "Price update failed; the previous prices remain in place."); }
            }
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }
}
