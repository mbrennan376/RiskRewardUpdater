using RiskReward.Core;
using RiskReward.Infrastructure;
using RiskReward.PriceService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "RiskReward Price Updater");
builder.Services.Configure<RiskRewardOptions>(builder.Configuration.GetSection("RiskReward"));
builder.Services.Configure<ProviderOptions>(builder.Configuration.GetSection("Providers"));
builder.Services.AddSingleton<StateStore>();
builder.Services.AddHttpClient<TwelveDataQuoteProvider>();
builder.Services.AddHttpClient<FinnhubQuoteProvider>();
builder.Services.AddSingleton<IQuoteProvider>(services => services.GetRequiredService<TwelveDataQuoteProvider>());
builder.Services.AddSingleton<IQuoteProvider>(services => services.GetRequiredService<FinnhubQuoteProvider>());
builder.Services.AddSingleton<PriceUpdateService>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
