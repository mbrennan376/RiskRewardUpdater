using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

public sealed class SitePublisher
{
    private readonly RiskRewardOptions options;
    private readonly StateStore store;

    public SitePublisher(IOptions<RiskRewardOptions> options, StateStore store)
    {
        this.options = options.Value;
        this.store = store;
    }

    public async Task<PublicationRecord> PublishApprovedAsync(DeploymentTarget target, CancellationToken cancellationToken = default)
    {
        if (target == DeploymentTarget.Live)
        {
            if (!options.AllowLivePublishing)
                throw new InvalidOperationException("Live publishing is disabled in configuration.");
        }

        var state = await store.LoadAsync(cancellationToken);
        var blocking = state.Drafts.Values.Where(d => !d.Ready && !d.Skipped).Select(d => d.TickerSymbol).ToList();
        if (blocking.Count > 0)
            throw new InvalidOperationException($"Review or skip every pending chart first: {string.Join(", ", blocking)}");
        var approved = state.Drafts.Values.Where(d => d.Ready && !d.Skipped).ToList();
        foreach (var draft in approved)
        {
            if (!File.Exists(draft.SourcePath) || await StateStore.HashFileAsync(draft.SourcePath, cancellationToken) != draft.ImageHash)
                throw new InvalidOperationException($"{draft.TickerSymbol} changed after review. Rescan and review the new image before publishing.");
        }

        var nextCharts = state.Charts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var draft in approved)
        {
            var extension = Path.GetExtension(draft.SourcePath).ToLowerInvariant();
            var assetHash = draft.UseEditedImage && !string.IsNullOrWhiteSpace(draft.EditedImageHash) ? draft.EditedImageHash : draft.ImageHash;
            if (draft.UseEditedImage) extension = ".png";
            var assetName = $"charts/{draft.TickerSymbol}-{assetHash[..12]}{extension}";
            nextCharts[draft.TickerSymbol] = new RiskRewardChart
            {
                TickerSymbol = draft.TickerSymbol,
                CompanyName = draft.CompanyName,
                UpdatedDate = DateOnly.FromDateTime(draft.SourceModifiedAt.LocalDateTime),
                ChartFilename = assetName,
                ImageHash = draft.ImageHash,
                UpperLine = draft.UpperLine,
                LowerLine = draft.LowerLine,
                Comments = draft.Comments,
                ProviderSymbols = new(draft.ProviderSymbols, StringComparer.OrdinalIgnoreCase),
                Analysis = draft.Analysis ?? new ChartAnalysis { SourceImageHash = draft.ImageHash }
            };
        }

        var catalog = new ChartCatalog { GeneratedAt = DateTimeOffset.UtcNow, Charts = nextCharts.Values.OrderBy(c => c.TickerSymbol).ToList() };
        var record = new PublicationRecord { PublishedAt = DateTimeOffset.UtcNow, Target = target, Tickers = approved.Select(d => d.TickerSymbol).ToList() };
        try
        {
            if (target == DeploymentTarget.Local) await PublishLocalAsync(catalog, approved, cancellationToken);
            else await PublishLiveAsync(catalog, approved, cancellationToken);
            record.Succeeded = true;

            foreach (var draft in approved)
            {
                store.ArchiveImage(draft.TickerSymbol, draft.ImageHash, draft.SourcePath);
                if (draft.UseEditedImage && !string.IsNullOrWhiteSpace(draft.EditedImagePath) && !string.IsNullOrWhiteSpace(draft.EditedImageHash))
                    store.ArchiveImage(draft.TickerSymbol, draft.EditedImageHash, draft.EditedImagePath);
                if (target == DeploymentTarget.Live)
                {
                    state.Charts[draft.TickerSymbol] = nextCharts[draft.TickerSymbol];
                    state.Drafts.Remove(draft.TickerSymbol);
                }
            }
        }
        catch (Exception ex)
        {
            record.Error = ex.Message;
            throw;
        }
        finally
        {
            state.Publications.Insert(0, record);
            if (state.Publications.Count > 100) state.Publications.RemoveRange(100, state.Publications.Count - 100);
            await store.SaveAsync(state, cancellationToken);
        }
        return record;
    }

    private async Task PublishLocalAsync(ChartCatalog catalog, List<ChartDraft> approved, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.LocalSitePath);
        CopySiteFiles(options.StaticSiteSourcePath, options.LocalSitePath);
        Directory.CreateDirectory(Path.Combine(options.LocalSitePath, "charts"));
        foreach (var draft in approved)
        {
            var chart = catalog.Charts.Single(c => c.TickerSymbol == draft.TickerSymbol);
            File.Copy(PublishPath(draft), Path.Combine(options.LocalSitePath, chart.ChartFilename.Replace('/', Path.DirectorySeparatorChar)), true);
        }
        // A clean local preview should also display legacy charts that were not in this approval batch.
        foreach (var chart in catalog.Charts)
        {
            var target = Path.Combine(options.LocalSitePath, chart.ChartFilename.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target)) continue;
            var legacySource = Directory.EnumerateFiles(options.ScreenshotFolder)
                .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(chart.TickerSymbol, StringComparison.OrdinalIgnoreCase));
            if (legacySource is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(legacySource, target, true);
            }
        }
        await WriteJsonAtomicallyAsync(Path.Combine(options.LocalSitePath, "data.json"), catalog, cancellationToken);
    }

    private async Task PublishLiveAsync(ChartCatalog catalog, List<ChartDraft> approved, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.AzureStorageConnectionString))
            throw new InvalidOperationException("The live Azure Storage connection string is not configured.");
        var container = new BlobContainerClient(options.AzureStorageConnectionString, options.LiveContainerName);
        foreach (var file in Directory.EnumerateFiles(options.StaticSiteSourcePath, "*", SearchOption.AllDirectories).Where(path => !IsExcluded(options.StaticSiteSourcePath, path)))
        {
            var relative = Path.GetRelativePath(options.StaticSiteSourcePath, file).Replace('\\', '/');
            if (relative.Equals("data.json", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("charts/", StringComparison.OrdinalIgnoreCase)) continue;
            await container.GetBlobClient(relative).UploadAsync(file, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = ContentType(file), CacheControl = relative.Equals("index.html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public, max-age=300" }
            }, cancellationToken);
        }
        foreach (var draft in approved)
        {
            var chart = catalog.Charts.Single(c => c.TickerSymbol == draft.TickerSymbol);
            await container.GetBlobClient(chart.ChartFilename).UploadAsync(PublishPath(draft), true, cancellationToken);
        }
        await using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, catalog, JsonDefaults.Options, cancellationToken);
        stream.Position = 0;
        await container.GetBlobClient("data.json").UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json", CacheControl = "no-cache" }
        }, cancellationToken);
    }

    private static void CopySiteFiles(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Static site source not found: {source}");
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                     .Where(path => !IsExcluded(source, path)))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Where(path => !IsExcluded(source, path)))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Equals("data.json", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("charts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static bool IsExcluded(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar).Any(part => part is ".git" or ".vs" or "node_modules");
    }

    private static string PublishPath(ChartDraft draft) => draft.UseEditedImage && !string.IsNullOrWhiteSpace(draft.EditedImagePath) ? draft.EditedImagePath : draft.SourcePath;

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json", ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".webmanifest" => "application/manifest+json", _ => "application/octet-stream"
    };

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options, cancellationToken);
        File.Move(temporary, path, true);
    }
}
