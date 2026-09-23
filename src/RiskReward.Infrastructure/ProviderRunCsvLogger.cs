using System.Globalization;
using Microsoft.Extensions.Options;

namespace RiskReward.Infrastructure;

public sealed class ProviderRunCsvLogger
{
    private const string Header = "SuccessCount,FailureCount,Provider,DateTime";
    private readonly string logFolder;
    private readonly object gate = new();

    public ProviderRunCsvLogger(IOptions<RiskRewardOptions> options) =>
        logFolder = Path.Combine(options.Value.StateFolder, "logs");

    public void Record(string provider, int requestedCount, int successCount)
    {
        var timestamp = DateTimeOffset.Now;
        var safeSuccess = Math.Clamp(successCount, 0, requestedCount);
        var line = string.Join(',', safeSuccess.ToString(CultureInfo.InvariantCulture),
            (requestedCount - safeSuccess).ToString(CultureInfo.InvariantCulture),
            Csv(provider), timestamp.ToString("O", CultureInfo.InvariantCulture));

        lock (gate)
        {
            Directory.CreateDirectory(logFolder);
            var path = Path.Combine(logFolder, $"provider-summary-{timestamp:yyyy-MM-dd}.csv");
            if (!File.Exists(path)) File.WriteAllText(path, Header + Environment.NewLine);
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public void DeleteLogsOlderThan(TimeSpan age)
    {
        if (!Directory.Exists(logFolder)) return;
        var cutoff = DateTime.UtcNow - age;
        lock (gate)
        {
            foreach (var pattern in new[] { "price-service-*.log", "provider-summary-*.csv" })
            foreach (var path in Directory.EnumerateFiles(logFolder, pattern, SearchOption.TopDirectoryOnly))
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
        }
    }

    private static string Csv(string value) => value.IndexOfAny([',', '\"', '\r', '\n']) >= 0
        ? $"\"{value.Replace("\"", "\"\"")}\""
        : value;
}
