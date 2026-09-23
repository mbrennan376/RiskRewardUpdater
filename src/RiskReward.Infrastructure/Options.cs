namespace RiskReward.Infrastructure;

public sealed class RiskRewardOptions
{
    public string ScreenshotFolder { get; set; } = @"C:\Junk\Charts\Charts";
    public string StateFolder { get; set; } = @"C:\ProgramData\RiskReward";
    public string StaticSiteSourcePath { get; set; } = @"C:\source\repos\riskrewardsite";
    public string LocalSitePath { get; set; } = @"C:\RiskReward\PreviewSite";
    public bool AllowLivePublishing { get; set; }
    public string LiveContainerName { get; set; } = "$web";
    public string? AzureStorageConnectionString { get; set; }
    public string PublicSiteUrl { get; set; } = "";
}

public sealed class ProviderOptions
{
    public string? TwelveDataApiKey { get; set; }
    public string? FinnhubApiKey { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 20;
}

public sealed class OpenAiOptions
{
    public string? ApiKey { get; set; }
    public string AnalysisModel { get; set; } = "gpt-6-astra";
    public string ImageEditModel { get; set; } = "gpt-image-2.5-sunburst";
    public int RequestTimeoutSeconds { get; set; } = 180;
}
