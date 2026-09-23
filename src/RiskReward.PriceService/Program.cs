using RiskReward.Core;
using RiskReward.Infrastructure;
using RiskReward.PriceService;

var runOnce = args.Any(argument => argument.Equals("--run-once", StringComparison.OrdinalIgnoreCase));
var deployArgument = Array.FindIndex(args, argument => argument.Equals("--deploy-site", StringComparison.OrdinalIgnoreCase));
var deploySitePath = deployArgument >= 0 && deployArgument + 1 < args.Length ? args[deployArgument + 1] : null;
if (deployArgument >= 0 && string.IsNullOrWhiteSpace(deploySitePath)) throw new ArgumentException("--deploy-site requires a source directory.");
var builder = Host.CreateApplicationBuilder(args);
// Provider credentials are query-string parameters; never emit HttpClient request URLs to logs.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
// Console run-once is a local-development tool, even when DOTNET_ENVIRONMENT defaults to Production.
// Explicitly load this project's user-secrets so API keys work without changing machine-wide environment state.
if (runOnce || deploySitePath is not null)
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
    // Re-add production sources afterward so server environment and explicit command-line values win.
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}
var stateFolder = builder.Configuration["RiskReward:StateFolder"] ?? @"C:\ProgramData\RiskReward";
builder.Logging.AddProvider(new DailyFileLoggerProvider(Path.Combine(stateFolder, "logs")));
builder.Services.AddWindowsService(options => options.ServiceName = "RiskReward Price Updater");
builder.Services.Configure<RiskRewardOptions>(builder.Configuration.GetSection("RiskReward"));
builder.Services.Configure<ProviderOptions>(builder.Configuration.GetSection("Providers"));
builder.Services.AddSingleton<StateStore>();
builder.Services.AddHttpClient<TwelveDataQuoteProvider>();
builder.Services.AddHttpClient<FinnhubQuoteProvider>();
builder.Services.AddSingleton<IQuoteProvider>(services => services.GetRequiredService<TwelveDataQuoteProvider>());
builder.Services.AddSingleton<IQuoteProvider>(services => services.GetRequiredService<FinnhubQuoteProvider>());
builder.Services.AddSingleton<ProviderRunCsvLogger>();
builder.Services.AddSingleton<PriceUpdateService>();
builder.Services.AddSingleton<StaticSiteDeploymentService>();
if (!runOnce && deploySitePath is null) builder.Services.AddHostedService<Worker>();

using var host = builder.Build();
if (runOnce || deploySitePath is not null)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RunOnce");
    try
    {
        if (deploySitePath is not null)
        {
            logger.LogInformation("Publishing packaged static-site assets to the LIVE site.");
            var uploaded = await host.Services.GetRequiredService<StaticSiteDeploymentService>().DeployAsync(deploySitePath);
            logger.LogInformation("Published {Count} static-site assets. Live chart data and images were preserved.", uploaded);
        }
        if (runOnce)
        {
            logger.LogInformation("Starting one price-update cycle, then the process will exit.");
            await host.Services.GetRequiredService<PriceUpdateService>().UpdateAsync();
            logger.LogInformation("The one-time price-update cycle completed.");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "The requested deployment/update operation failed.");
        Environment.ExitCode = 1;
    }
    return;
}

await host.RunAsync();
