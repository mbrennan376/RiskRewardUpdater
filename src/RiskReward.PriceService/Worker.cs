using RiskReward.Infrastructure;

namespace RiskReward.PriceService;

public sealed class Worker(PriceUpdateService updater, ILogger<Worker> logger) : BackgroundService
{
    private readonly TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private string? lastSlot;
    private DateOnly? completedFinalUpdate;
    private DateTimeOffset? nextFinalAttempt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Risk/Reward price service started; the configured deployment target is checked before every update.");
        var startupResult = await RunUpdateAsync("startup price refresh", stoppingToken);
        var startupEastern = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, eastern);
        if (MarketSchedule.IsFinalRetryWindow(startupEastern) && startupResult?.AllFresh == true)
            completedFinalUpdate = DateOnly.FromDateTime(startupEastern.Date);
        while (!stoppingToken.IsCancellationRequested)
        {
            var easternNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, eastern);
            var slot = MarketSchedule.SlotKey(easternNow);
            if (MarketSchedule.IsPollingSlot(easternNow) && slot != lastSlot)
            {
                lastSlot = slot;
                if (easternNow.Hour == 16) nextFinalAttempt = easternNow.AddMinutes(5);
                var result = await RunUpdateAsync($"scheduled price update for {slot} Eastern", stoppingToken);
                if (easternNow.Hour == 16 && result?.AllFresh == true) completedFinalUpdate = DateOnly.FromDateTime(easternNow.Date);
            }
            else if (MarketSchedule.IsFinalRetryWindow(easternNow) &&
                     completedFinalUpdate != DateOnly.FromDateTime(easternNow.Date) &&
                     (nextFinalAttempt is null || easternNow >= nextFinalAttempt))
            {
                nextFinalAttempt = easternNow.AddMinutes(5);
                var result = await RunUpdateAsync($"final-close retry for {easternNow:yyyy-MM-dd HH:mm} Eastern", stoppingToken);
                if (result?.AllFresh == true) completedFinalUpdate = DateOnly.FromDateTime(easternNow.Date);
            }
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private async Task<PriceUpdateResult?> RunUpdateAsync(string description, CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting {Description}.", description);
        try
        {
            var result = await updater.UpdateAsync(stoppingToken);
            logger.LogInformation("Completed {Description}: {FreshCount}/{TotalCount} fresh prices published to {Target}.",
                description, result.FreshCount, result.TotalCount, result.Target);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The {Description} failed; the previous prices remain in place.", description);
            return null;
        }
    }
}
