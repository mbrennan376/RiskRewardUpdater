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

    [Fact]
    public async Task RemovalStaysPendingUntilALivePublication()
    {
        var options = CreateOptions();
        var (store, review, publisher) = await CreateSiteWithTwoChartsAsync(options);

        Assert.True(await review.MarkForRemovalAsync("abc"));
        Assert.False(await review.MarkForRemovalAsync("NOPE"));
        var result = await publisher.PublishApprovedAsync(DeploymentTarget.Local);

        Assert.Equal(new[] { "ABC" }, result.RemovedTickers);
        var preview = await File.ReadAllTextAsync(Path.Combine(options.LocalSitePath, "data.json"));
        Assert.DoesNotContain("ABC Corp", preview);
        Assert.Contains("XYZ Corp", preview);
        // A local preview decides nothing: the chart is still published and still marked.
        var state = await store.LoadAsync();
        Assert.True(state.Charts.ContainsKey("ABC"));
        Assert.Equal(new[] { "ABC" }, state.PendingRemovals);
        Assert.Equal(new[] { "ABC" }, await review.PendingRemovalsAsync());
    }

    [Fact]
    public async Task UndoKeepsTheChart()
    {
        var options = CreateOptions();
        var (store, review, publisher) = await CreateSiteWithTwoChartsAsync(options);
        await review.MarkForRemovalAsync("ABC");

        Assert.True(await review.UndoRemovalAsync("abc"));
        Assert.False(await review.UndoRemovalAsync("ABC"));
        var result = await publisher.PublishApprovedAsync(DeploymentTarget.Local);

        Assert.Empty(result.RemovedTickers);
        Assert.Contains("ABC Corp", await File.ReadAllTextAsync(Path.Combine(options.LocalSitePath, "data.json")));
        Assert.Empty((await store.LoadAsync()).PendingRemovals);
    }

    [Fact]
    public async Task RemovalSkipsAWaitingScreenshotAndReadyAgainCancelsIt()
    {
        var options = CreateOptions();
        var (store, review, publisher) = await CreateSiteWithTwoChartsAsync(options);
        // A new screenshot for ABC is waiting for review.
        await File.WriteAllBytesAsync(Path.Combine(options.ScreenshotFolder, "ABC.png"), [9, 9, 9]);
        Assert.Single(await review.ScanAsync());

        await review.MarkForRemovalAsync("ABC");
        var skipped = Assert.Single(await review.ScanAsync());
        Assert.True(skipped.Skipped);
        // Not blocked by the waiting screenshot, and the chart leaves the catalog.
        var removed = await publisher.PublishApprovedAsync(DeploymentTarget.Local);
        Assert.Equal(new[] { "ABC" }, removed.RemovedTickers);

        // Marking the chart ready afterwards is the newer decision.
        skipped.Ready = true;
        skipped.Skipped = false;
        skipped.LowerLine = 1m;
        skipped.UpperLine = 2m;
        await review.SaveDraftAsync("ABC", skipped);
        Assert.Empty((await store.LoadAsync()).PendingRemovals);
        var kept = await publisher.PublishApprovedAsync(DeploymentTarget.Local);
        Assert.Empty(kept.RemovedTickers);
        Assert.Equal(new[] { "ABC" }, kept.Tickers);
    }

    [Fact]
    public async Task ReportsPublishedChartsWhoseScreenshotWasDeleted()
    {
        var options = CreateOptions();
        var (_, review, _) = await CreateSiteWithTwoChartsAsync(options);

        // An empty folder (Drive not synced yet) reports nothing.
        Assert.Empty(await review.MissingScreenshotsAsync());

        await File.WriteAllBytesAsync(Path.Combine(options.ScreenshotFolder, "XYZ.png"), [1]);
        Assert.Equal(new[] { "ABC" }, await review.MissingScreenshotsAsync());

        await review.MarkForRemovalAsync("ABC");
        Assert.Empty(await review.MissingScreenshotsAsync());
    }

    private static async Task<(StateStore Store, ChartReviewService Review, SitePublisher Publisher)> CreateSiteWithTwoChartsAsync(RiskRewardOptions options)
    {
        Directory.CreateDirectory(options.StaticSiteSourcePath);
        Directory.CreateDirectory(options.ScreenshotFolder);
        await File.WriteAllTextAsync(Path.Combine(options.StaticSiteSourcePath, "index.html"), "site");
        var store = new StateStore(Options.Create(options));
        await store.SaveAsync(new ApplicationState
        {
            Charts = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ABC"] = new RiskRewardChart { TickerSymbol = "ABC", CompanyName = "ABC Corp", ChartFilename = "charts/ABC.png", ImageHash = "old" },
                ["XYZ"] = new RiskRewardChart { TickerSymbol = "XYZ", CompanyName = "XYZ Corp", ChartFilename = "charts/XYZ.png" }
            }
        });
        return (store, new ChartReviewService(Options.Create(options), store), new SitePublisher(Options.Create(options), store));
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
