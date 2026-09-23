using Microsoft.Extensions.Options;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class ProviderRunCsvLoggerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"risk-reward-provider-log-{Guid.NewGuid():N}");

    [Fact]
    public void WritesDailyCsvWithRequestedSummaryShape()
    {
        var logger = CreateLogger();

        logger.Record("twelveData", 19, 19);

        var lines = File.ReadAllLines(Directory.GetFiles(Path.Combine(root, "logs"), "provider-summary-*.csv").Single());
        Assert.Equal("SuccessCount,FailureCount,Provider,DateTime", lines[0]);
        Assert.StartsWith("19,0,twelveData,", lines[1]);
        Assert.True(DateTimeOffset.TryParse(lines[1].Split(',', 4)[3], out _));
    }

    [Fact]
    public void DeletesBothManagedLogTypesAfterThirtyDays()
    {
        var folder = Path.Combine(root, "logs");
        Directory.CreateDirectory(folder);
        var appLog = Path.Combine(folder, "price-service-2026-01-01.log");
        var providerLog = Path.Combine(folder, "provider-summary-2026-01-01.csv");
        File.WriteAllText(appLog, "old");
        File.WriteAllText(providerLog, "old");
        File.SetLastWriteTimeUtc(appLog, DateTime.UtcNow.AddDays(-31));
        File.SetLastWriteTimeUtc(providerLog, DateTime.UtcNow.AddDays(-31));

        CreateLogger().DeleteLogsOlderThan(TimeSpan.FromDays(30));

        Assert.False(File.Exists(appLog));
        Assert.False(File.Exists(providerLog));
    }

    private ProviderRunCsvLogger CreateLogger() => new(Options.Create(new RiskRewardOptions { StateFolder = root }));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
