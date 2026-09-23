using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using RiskReward.Core;
using RiskReward.Infrastructure;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5188");
builder.Services.Configure<RiskRewardOptions>(builder.Configuration.GetSection("RiskReward"));
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<ChartReviewService>();
builder.Services.AddSingleton<SitePublisher>();
builder.Services.AddHttpClient<OpenAiChartAnalyzer>();
builder.Services.AddHttpClient<OpenAiChartImageEditor>();
builder.Services.AddSingleton<IChartAnalyzer>(services => services.GetRequiredService<OpenAiChartAnalyzer>());
builder.Services.AddSingleton<IChartImageEditor>(services => services.GetRequiredService<OpenAiChartImageEditor>());

var app = builder.Build();
var configured = app.Services.GetRequiredService<IOptions<RiskRewardOptions>>().Value;
var openAiConfigured = app.Services.GetRequiredService<IOptions<OpenAiOptions>>().Value;
Directory.CreateDirectory(configured.LocalSitePath);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseFileServer(new FileServerOptions
{
    RequestPath = "/preview",
    FileProvider = new PhysicalFileProvider(configured.LocalSitePath),
    EnableDefaultFiles = true,
    EnableDirectoryBrowsing = false
});

app.MapGet("/api/status", async (StateStore store, CancellationToken cancellationToken) =>
{
    var target = await store.GetTargetAsync(cancellationToken);
    return Results.Ok(new { target, configured.AllowLivePublishing, configured.ScreenshotFolder, configured.LocalSitePath, configured.LiveContainerName, previewUrl = "/preview/", openAiEnabled = !string.IsNullOrWhiteSpace(openAiConfigured.ApiKey), openAiConfigured.AnalysisModel, openAiConfigured.ImageEditModel });
});

app.MapPut("/api/target", async (TargetRequest request, StateStore store, CancellationToken cancellationToken) =>
{
    if (request.Target == DeploymentTarget.Live)
    {
        if (!configured.AllowLivePublishing) return Results.BadRequest(new { error = "Live publishing is disabled in appsettings.json." });
        if (request.Confirmation != "PUBLISH LIVE") return Results.BadRequest(new { error = "Enter PUBLISH LIVE to enable live mode." });
    }
    await store.SetTargetAsync(request.Target, cancellationToken);
    return Results.Ok(new { target = request.Target });
});

app.MapGet("/api/charts/pending", async (ChartReviewService review, CancellationToken cancellationToken) => Results.Ok(await review.ScanAsync(cancellationToken)));

app.MapGet("/api/source-image/{ticker}", async (string ticker, StateStore store, CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    if (!state.Drafts.TryGetValue(ticker, out var draft) || !File.Exists(draft.SourcePath)) return Results.NotFound();
    var extension = Path.GetExtension(draft.SourcePath).ToLowerInvariant();
    return Results.File(draft.SourcePath, extension is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png", enableRangeProcessing: true);
});

app.MapGet("/api/edited-image/{ticker}", async (string ticker, StateStore store, CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    if (!state.Drafts.TryGetValue(ticker, out var draft) || string.IsNullOrWhiteSpace(draft.EditedImagePath) || !File.Exists(draft.EditedImagePath)) return Results.NotFound();
    return Results.File(draft.EditedImagePath, "image/png", enableRangeProcessing: true);
});

app.MapPut("/api/charts/{ticker}", async (string ticker, ChartDraft input, ChartReviewService review, CancellationToken cancellationToken) =>
{
    try
    {
        var saved = await review.SaveDraftAsync(ticker, input, cancellationToken);
        return saved is null ? Results.NotFound() : Results.Ok(saved);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/charts/{ticker}/analyze", async (string ticker, StateStore store, ChartReviewService review, IChartAnalyzer analyzer, CancellationToken cancellationToken) =>
{
    try
    {
        var state = await store.LoadAsync(cancellationToken);
        if (!state.Drafts.TryGetValue(ticker, out var draft)) return Results.NotFound();
        var analysis = await analyzer.AnalyzeAsync(draft.SourcePath, cancellationToken);
        return Results.Ok(await review.ApplyAnalysisAsync(ticker, analysis, cancellationToken));
    }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/charts/{ticker}/remove-video", async (string ticker, StateStore store, ChartReviewService review, IChartImageEditor editor, CancellationToken cancellationToken) =>
{
    try
    {
        var state = await store.LoadAsync(cancellationToken);
        if (!state.Drafts.TryGetValue(ticker, out var draft)) return Results.NotFound();
        var folder = Path.Combine(configured.StateFolder, "ai-edits");
        var output = Path.Combine(folder, $"{draft.TickerSymbol}-{draft.ImageHash[..12]}.png");
        await editor.RemoveVideoBoxAsync(draft.SourcePath, output, cancellationToken);
        return Results.Ok(await review.SetEditedImageAsync(ticker, output, cancellationToken));
    }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPut("/api/charts/{ticker}/image-choice", async (string ticker, ImageChoiceRequest request, ChartReviewService review, CancellationToken cancellationToken) =>
{
    try
    {
        var draft = await review.ChooseImageAsync(ticker, request.UseEditedImage, cancellationToken);
        return draft is null ? Results.NotFound() : Results.Ok(draft);
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/publish", async (PublishRequest request, StateStore store, SitePublisher publisher, CancellationToken cancellationToken) =>
{
    try
    {
        var target = await store.GetTargetAsync(cancellationToken);
        return Results.Ok(await publisher.PublishApprovedAsync(target, request.Confirmation, cancellationToken));
    }
    catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapFallbackToFile("index.html");
app.Run();

public sealed record TargetRequest(DeploymentTarget Target, string? Confirmation);
public sealed record PublishRequest(string? Confirmation);
public sealed record ImageChoiceRequest(bool UseEditedImage);
