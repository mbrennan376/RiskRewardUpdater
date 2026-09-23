using System.Text.Json;
using Microsoft.Extensions.Options;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

public sealed class ChartReviewService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };
    private readonly RiskRewardOptions options;
    private readonly StateStore store;

    public ChartReviewService(IOptions<RiskRewardOptions> options, StateStore store)
    {
        this.options = options.Value;
        this.store = store;
    }

    public async Task<IReadOnlyList<ChartDraft>> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(options.ScreenshotFolder))
            throw new DirectoryNotFoundException($"Screenshot folder not found: {options.ScreenshotFolder}");

        var state = await store.LoadAsync(cancellationToken);
        await ImportLegacyMetadataAsync(state, cancellationToken);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(options.ScreenshotFolder)
                     .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                     .Where(path => !Path.GetFileName(path).StartsWith("b_", StringComparison.OrdinalIgnoreCase)))
        {
            var ticker = Path.GetFileNameWithoutExtension(path).Trim().ToUpperInvariant();
            var hash = await StateStore.HashFileAsync(path, cancellationToken);
            found.Add(ticker);
            if (state.Charts.TryGetValue(ticker, out var published) && published.ImageHash == hash)
            {
                state.Drafts.Remove(ticker);
                continue;
            }

            state.Drafts.TryGetValue(ticker, out var previousDraft);
            if (previousDraft is null || previousDraft.ImageHash != hash)
            {
                var draft = new ChartDraft
                {
                    TickerSymbol = ticker,
                    CompanyName = published?.CompanyName ?? ticker,
                    SourcePath = path,
                    ImageHash = hash,
                    SourceModifiedAt = File.GetLastWriteTimeUtc(path),
                    UpperLine = previousDraft?.UpperLine ?? published?.UpperLine,
                    LowerLine = previousDraft?.LowerLine ?? published?.LowerLine,
                    Comments = previousDraft?.Comments ?? published?.Comments ?? "",
                    ProviderSymbols = (previousDraft?.ProviderSymbols ?? published?.ProviderSymbols) is not { } symbols
                        ? new(StringComparer.OrdinalIgnoreCase)
                        : new(symbols, StringComparer.OrdinalIgnoreCase)
                };
                state.Drafts[ticker] = draft;
            }
            else
            {
                previousDraft.SourcePath = path;
                previousDraft.SourceModifiedAt = File.GetLastWriteTimeUtc(path);
            }
        }

        foreach (var stale in state.Drafts.Keys.Where(key => !found.Contains(key)).ToList())
            state.Drafts.Remove(stale);

        await store.SaveAsync(state, cancellationToken);
        return state.Drafts.Values.OrderByDescending(d => d.SourceModifiedAt).ThenBy(d => d.TickerSymbol).ToList();
    }

    public async Task<ChartDraft?> SaveDraftAsync(string originalTicker, ChartDraft input, CancellationToken cancellationToken = default)
    {
        var state = await store.LoadAsync(cancellationToken);
        if (!state.Drafts.TryGetValue(originalTicker, out var existing)) return null;
        var ticker = input.TickerSymbol.Trim().ToUpperInvariant();
        if (ticker.Length is < 1 or > 15) throw new ArgumentException("Enter a valid ticker.");
        if (input.Ready && (input.LowerLine is null || input.UpperLine is null || input.LowerLine <= 0 || input.UpperLine <= input.LowerLine))
            throw new ArgumentException("Ready charts require a positive lower line and a larger upper line.");

        existing.TickerSymbol = ticker;
        existing.CompanyName = string.IsNullOrWhiteSpace(input.CompanyName) ? ticker : input.CompanyName.Trim();
        existing.LowerLine = input.LowerLine;
        existing.UpperLine = input.UpperLine;
        existing.Comments = input.Comments?.Trim() ?? "";
        existing.ProviderSymbols = input.ProviderSymbols
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        existing.Ready = input.Ready;
        existing.Skipped = input.Skipped;
        if (!ticker.Equals(originalTicker, StringComparison.OrdinalIgnoreCase))
        {
            state.Drafts.Remove(originalTicker);
            state.Drafts[ticker] = existing;
        }
        await store.SaveAsync(state, cancellationToken);
        return existing;
    }

    public async Task<ChartDraft?> ApplyAnalysisAsync(string ticker, ChartAnalysis analysis, CancellationToken cancellationToken = default)
    {
        var state = await store.LoadAsync(cancellationToken);
        if (!state.Drafts.TryGetValue(ticker, out var draft)) return null;
        analysis.SourceImageHash = draft.ImageHash;
        draft.Analysis = analysis;
        if (analysis.Status == "suggested")
        {
            draft.UpperLine = analysis.SuggestedUpperLine;
            draft.LowerLine = analysis.SuggestedLowerLine;
        }
        draft.Ready = false;
        await store.SaveAsync(state, cancellationToken);
        return draft;
    }

    public async Task<ChartDraft?> SetEditedImageAsync(string ticker, string editedPath, CancellationToken cancellationToken = default)
    {
        var state = await store.LoadAsync(cancellationToken);
        if (!state.Drafts.TryGetValue(ticker, out var draft)) return null;
        draft.EditedImagePath = editedPath;
        draft.EditedImageHash = await StateStore.HashFileAsync(editedPath, cancellationToken);
        draft.UseEditedImage = false;
        draft.Ready = false;
        await store.SaveAsync(state, cancellationToken);
        return draft;
    }

    public async Task<ChartDraft?> ChooseImageAsync(string ticker, bool useEditedImage, CancellationToken cancellationToken = default)
    {
        var state = await store.LoadAsync(cancellationToken);
        if (!state.Drafts.TryGetValue(ticker, out var draft)) return null;
        if (useEditedImage && (string.IsNullOrWhiteSpace(draft.EditedImagePath) || !File.Exists(draft.EditedImagePath)))
            throw new InvalidOperationException("Generate an edited image before selecting it.");
        draft.UseEditedImage = useEditedImage;
        draft.Ready = false;
        await store.SaveAsync(state, cancellationToken);
        return draft;
    }

    private async Task ImportLegacyMetadataAsync(ApplicationState state, CancellationToken cancellationToken)
    {
        if (state.Charts.Count > 0) return;
        var path = Path.Combine(options.ScreenshotFolder, "data.json");
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var rows = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                : document.RootElement.TryGetProperty("charts", out var charts) ? charts.EnumerateArray() : default;
            foreach (var row in rows)
            {
                var ticker = Read(row, "TickerSymbol", "Ticker Symbol").ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(ticker)) continue;
                DateOnly.TryParse(Read(row, "UpdatedDate", "Updated Date"), out var updated);
                state.Charts[ticker] = new RiskRewardChart
                {
                    TickerSymbol = ticker,
                    CompanyName = Read(row, "CompanyName", "Company Name") is { Length: > 0 } company ? company : ticker,
                    UpdatedDate = updated,
                    ChartFilename = Read(row, "ChartFilename", "Chart Filename"),
                    Comments = Read(row, "Comments")
                };
            }
        }
        catch (JsonException) { /* A malformed legacy file should not prevent screenshot review. */ }
    }

    private static string Read(JsonElement row, params string[] names)
    {
        foreach (var name in names)
            if (row.TryGetProperty(name, out var value)) return value.GetString() ?? "";
        return "";
    }
}
