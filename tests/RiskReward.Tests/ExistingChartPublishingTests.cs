using Microsoft.Extensions.Options;
using RiskReward.Core;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class ExistingChartPublishingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"risk-reward-existing-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReopensUnchangedChartWithPublishedBoundaries()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.ScreenshotFolder);
        var image = Path.Combine(options.ScreenshotFolder, "ABC.png");
        await File.WriteAllBytesAsync(image, [1, 2, 3]);
        var store = new StateStore(Options.Create(options));
        await store.SaveAsync(new ApplicationState
        {
            Charts = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ABC"] = new RiskRewardChart { TickerSymbol = "ABC", CompanyName = "ABC Corp", ImageHash = await StateStore.HashFileAsync(image), LowerLine = 12.5m, UpperLine = 40m }
            }
        });
        var review = new ChartReviewService(Options.Create(options), store);

        var draft = await review.ReopenPublishedAsync("ABC");
        var rescanned = await review.ScanAsync();

        Assert.NotNull(draft);
        Assert.True(draft.ManualReview);
        Assert.Equal(12.5m, draft.LowerLine);
        Assert.Equal(40m, draft.UpperLine);
        Assert.Single(rescanned);
    }

    [Fact]
    public async Task PublishesSiteAssetsWhenNoChartsArePending()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.StaticSiteSourcePath);
        Directory.CreateDirectory(options.ScreenshotFolder);
        await File.WriteAllBytesAsync(Path.Combine(options.ScreenshotFolder, "ABC.png"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(options.StaticSiteSourcePath, "index.html"), "updated site");
        var store = new StateStore(Options.Create(options));
        await store.SaveAsync(new ApplicationState
        {
            Charts = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ABC"] = new RiskRewardChart { TickerSymbol = "ABC", CompanyName = "ABC Corp", ChartFilename = "charts/ABC.png" }
            }
        });
        var publisher = new SitePublisher(Options.Create(options), store);

        var result = await publisher.PublishApprovedAsync(DeploymentTarget.Local);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Tickers);
        Assert.Equal("updated site", await File.ReadAllTextAsync(Path.Combine(options.LocalSitePath, "index.html")));
        Assert.True(File.Exists(Path.Combine(options.LocalSitePath, "data.json")));
    }

    private RiskRewardOptions CreateOptions() => new()
    {
        StateFolder = Path.Combine(root, "state"),
        ScreenshotFolder = Path.Combine(root, "screenshots"),
        StaticSiteSourcePath = Path.Combine(root, "site-source"),
        LocalSitePath = Path.Combine(root, "preview")
    };

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
