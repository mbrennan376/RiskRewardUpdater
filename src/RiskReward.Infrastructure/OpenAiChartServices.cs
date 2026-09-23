using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

public sealed class OpenAiChartAnalyzer : IChartAnalyzer
{
    private readonly HttpClient http;
    private readonly OpenAiOptions options;

    public OpenAiChartAnalyzer(HttpClient http, IOptions<OpenAiOptions> options)
    {
        this.http = http;
        this.options = options.Value;
        this.http.Timeout = TimeSpan.FromSeconds(this.options.RequestTimeoutSeconds);
    }

    public async Task<ChartAnalysis> AnalyzeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        var mediaType = extension is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
        var dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(imagePath, cancellationToken))}";
        var request = new
        {
            model = options.AnalysisModel,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = AnalysisPrompt },
                        new { type = "input_image", image_url = dataUrl, detail = "high" }
                    }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema", name = "risk_reward_line_estimate", strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            canDetermine = new { type = "boolean" },
                            upperLine = new { type = "number" },
                            lowerLine = new { type = "number" },
                            confidence = new { type = "number", minimum = 0, maximum = 1 },
                            explanation = new { type = "string" }
                        },
                        required = new[] { "canDetermine", "upperLine", "lowerLine", "confidence", "explanation" },
                        additionalProperties = false
                    }
                }
            }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = JsonContent.Create(request) };
        Authorize(message);
        using var response = await http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw OpenAiError(response, body);
        using var document = JsonDocument.Parse(body);
        var outputText = ExtractOutputText(document.RootElement) ?? throw new InvalidOperationException("OpenAI returned no analysis text.");
        var estimate = JsonSerializer.Deserialize<LineEstimate>(outputText, JsonDefaults.Options) ?? throw new InvalidOperationException("OpenAI returned an invalid analysis.");
        var usable = estimate.CanDetermine && estimate.LowerLine > 0 && estimate.UpperLine > estimate.LowerLine;
        return new ChartAnalysis
        {
            Status = usable ? "suggested" : "unable-to-determine",
            Provider = "OpenAI",
            Model = options.AnalysisModel,
            SuggestedUpperLine = usable ? estimate.UpperLine : null,
            SuggestedLowerLine = usable ? estimate.LowerLine : null,
            Confidence = Math.Clamp(estimate.Confidence, 0, 1),
            Explanation = estimate.Explanation,
            AnalyzedAt = DateTimeOffset.UtcNow,
            AcceptedByUser = false
        };
    }

    private const string AnalysisPrompt = """
        Analyze this stock risk/reward screenshot as a visual assistant. Identify the two valuation boundary lines at the current/right edge of the chart: the numeric upper boundary and numeric lower boundary. Line colors are not reliable and may be reversed, so use vertical price position, not red/green color. Read the logarithmic price axis carefully. Do not infer a target from moving averages or channel lines. If the two intended boundaries or their current values cannot be read reliably, set canDetermine to false and both values to 0. This is only a draft for human review, not financial advice.
        """;

    private static string? ExtractOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output)) return null;
        foreach (var item in output.EnumerateArray())
            if (item.TryGetProperty("content", out var content))
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" && part.TryGetProperty("text", out var text)) return text.GetString();
        return null;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new InvalidOperationException("OpenAI is not configured. Set OpenAI:ApiKey using user-secrets or a protected environment variable.");
    }
    private void Authorize(HttpRequestMessage request) => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    private static Exception OpenAiError(HttpResponseMessage response, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var message = document.RootElement.GetProperty("error").GetProperty("message").GetString();
            return new InvalidOperationException($"OpenAI request failed ({(int)response.StatusCode}): {message}");
        }
        catch (JsonException) { return new InvalidOperationException($"OpenAI request failed ({(int)response.StatusCode})."); }
    }
    private sealed class LineEstimate { public bool CanDetermine { get; set; } public decimal UpperLine { get; set; } public decimal LowerLine { get; set; } public decimal Confidence { get; set; } public string Explanation { get; set; } = ""; }
}

public sealed class OpenAiChartImageEditor : IChartImageEditor
{
    private readonly HttpClient http;
    private readonly OpenAiOptions options;

    public OpenAiChartImageEditor(HttpClient http, IOptions<OpenAiOptions> options)
    {
        this.http = http;
        this.options = options.Value;
        this.http.Timeout = TimeSpan.FromSeconds(this.options.RequestTimeoutSeconds);
    }

    public async Task<string> RemoveVideoBoxAsync(string imagePath, string outputPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new InvalidOperationException("OpenAI is not configured. Set OpenAI:ApiKey using user-secrets or a protected environment variable.");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(options.ImageEditModel), "model");
        form.Add(new StringContent(EditPrompt), "prompt");
        form.Add(new StringContent("auto"), "size");
        form.Add(new StringContent("medium"), "quality");
        form.Add(new StringContent("png"), "output_format");
        var image = new ByteArrayContent(await File.ReadAllBytesAsync(imagePath, cancellationToken));
        image.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(imagePath) is ".jpg" or ".jpeg" or ".JPG" or ".JPEG" ? "image/jpeg" : "image/png");
        form.Add(image, "image[]", Path.GetFileName(imagePath));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/edits") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenAI image edit failed ({(int)response.StatusCode}): {ReadError(body)}");
        using var document = JsonDocument.Parse(body);
        var encoded = document.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
        if (string.IsNullOrWhiteSpace(encoded)) throw new InvalidOperationException("OpenAI returned no edited image.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporary = outputPath + ".tmp";
        await File.WriteAllBytesAsync(temporary, Convert.FromBase64String(encoded), cancellationToken);
        File.Move(temporary, outputPath, true);
        return outputPath;
    }

    private const string EditPrompt = """
        Preserve this financial chart exactly, including its aspect ratio, axes, price labels, dates, ticker, plotted price history, moving averages, risk/reward lines, annotations, and all other pixels. Remove only the embedded presenter/webcam video rectangle containing a person in the lower-left corner. Reconstruct the obscured chart background, grid, and any plot continuation conservatively from the immediately surrounding chart. Do not add, remove, reposition, recolor, or reinterpret any financial line or text outside that embedded video rectangle.
        """;

    private static string ReadError(string body)
    {
        try { using var document = JsonDocument.Parse(body); return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "Unknown error"; }
        catch { return "Unknown error"; }
    }
}
