using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace RiskReward.Infrastructure;

public sealed class StaticSiteDeploymentService(IOptions<RiskRewardOptions> configured)
{
    private readonly RiskRewardOptions options = configured.Value;

    public async Task<int> DeployAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!options.AllowLivePublishing) throw new InvalidOperationException("Live publishing is disabled in configuration.");
        if (string.IsNullOrWhiteSpace(options.AzureStorageConnectionString)) throw new InvalidOperationException("Azure Storage is not configured.");
        if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException($"Packaged static site not found: {sourcePath}");

        var container = new BlobContainerClient(options.AzureStorageConnectionString, options.LiveContainerName);
        var uploaded = 0;
        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is ".git" or ".vs" or "node_modules")) continue;
            // Catalog, quote, and chart content are managed by the review and price workflows.
            if (relative.Equals("data.json", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("prices.json", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("history/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("charts/", StringComparison.OrdinalIgnoreCase)) continue;

            await container.GetBlobClient(relative).UploadAsync(file, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = ContentType(file),
                    CacheControl = relative.Equals("index.html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public, max-age=300"
                }
            }, cancellationToken);
            uploaded++;
        }
        return uploaded;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webmanifest" => "application/manifest+json",
        _ => "application/octet-stream"
    };
}
