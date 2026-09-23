using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using RiskReward.Infrastructure;

namespace RiskReward.Tests;

public sealed class OpenAiChartServicesTests
{
    [Fact]
    public async Task AnalyzerParsesStructuredSuggestion()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            {"output":[{"content":[{"type":"output_text","text":"{\"canDetermine\":true,\"upperLine\":42.5,\"lowerLine\":12.25,\"confidence\":0.8,\"explanation\":\"Readable labels\"}"}]}]}
            """));
        using var image = new TemporaryImage();
        var service = new OpenAiChartAnalyzer(new HttpClient(handler), ConfiguredOptions());

        var result = await service.AnalyzeAsync(image.Path);

        Assert.Equal("suggested", result.Status);
        Assert.Equal(42.5m, result.SuggestedUpperLine);
        Assert.Equal(12.25m, result.SuggestedLowerLine);
        Assert.Equal("Bearer test-key", handler.Authorization);
        Assert.Contains("risk_reward_line_estimate", handler.RequestBody);
    }

    [Fact]
    public async Task ImageEditorWritesCandidateAndUsesEditEndpoint()
    {
        var expected = new byte[] { 1, 2, 3, 4 };
        var handler = new StubHandler(_ => JsonResponse($"{{\"data\":[{{\"b64_json\":\"{Convert.ToBase64String(expected)}\"}}]}}"));
        using var image = new TemporaryImage();
        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"risk-reward-{Guid.NewGuid():N}.png");
        try
        {
            var service = new OpenAiChartImageEditor(new HttpClient(handler), ConfiguredOptions());
            await service.RemoveVideoBoxAsync(image.Path, output);

            Assert.Equal(expected, await File.ReadAllBytesAsync(output));
            Assert.Equal("https://api.openai.com/v1/images/edits", handler.RequestUri);
            Assert.Contains("name=image[]", handler.RequestBody.Replace("\"", ""));
            Assert.Contains("gpt-image-2.5-sunburst", handler.RequestBody);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private static IOptions<OpenAiOptions> ConfiguredOptions() => Options.Create(new OpenAiOptions { ApiKey = "test-key" });
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";
        public string? Authorization { get; private set; }
        public string? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            RequestUri = request.RequestUri?.ToString();
            return response(request);
        }
    }

    private sealed class TemporaryImage : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"risk-reward-{Guid.NewGuid():N}.png");
        public TemporaryImage() => File.WriteAllBytes(Path, new byte[] { 137, 80, 78, 71 });
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
