# Risk/Reward Publisher

This solution contains a localhost chart-review site, shared chart/price infrastructure, and a Windows price-update service. The public dashboard remains a static site in `C:\source\repos\riskrewardsite`.

## Safe development workflow

1. Run `dotnet run --project RiskRewardUpdater.csproj` and open `http://127.0.0.1:5188`.
2. Review every changed screenshot. Enter numeric **upper** and **lower** lines; line color is intentionally ignored.
   - Optionally ask OpenAI to suggest both values. Suggested values are copied into the inputs but the chart is returned to draft status for your review.
   - Optionally ask OpenAI to remove the lower-left presenter video box. The generated candidate is never selected automatically; compare it with the original and explicitly choose which image to publish.
3. Mark each screenshot ready or skipped, then publish. The default target is `C:\RiskReward\PreviewSite` and is available at `/preview/` from the admin app.
4. Verify the complete static site locally.
5. Set `RiskReward:AllowLivePublishing` to `true` and configure `RiskReward:AzureStorageConnectionString` with .NET user-secrets. The target switch defaults to Live Azure on startup and can be changed to Local Preview immediately, without confirmation gates. A completion message is shown after publishing succeeds.

The shared deployment target is stored under `RiskReward:StateFolder`. The Windows Service reads that target before each update, so it also stays local until Live is deliberately enabled.

## Secrets

Do not commit API keys or Azure credentials. For local development:

```powershell
dotnet user-secrets set "RiskReward:AzureStorageConnectionString" "..." --project RiskRewardUpdater.csproj
dotnet user-secrets set "Providers:TwelveDataApiKey" "..." --project RiskRewardUpdater.csproj
dotnet user-secrets set "Providers:FinnhubApiKey" "..." --project RiskRewardUpdater.csproj
dotnet user-secrets set "OpenAI:ApiKey" "..." --project RiskRewardUpdater.csproj
```

Use equivalent environment variables or a protected service configuration for the Windows Service. Confirm that each market-data plan permits public display before enabling live price publication.

The OpenAI actions are entirely optional and disabled until an API key is configured. Line analysis uses the Responses API with a structured JSON result; video-box removal uses the Images edit API and saves the candidate under the local state folder. Model names can be changed with `OpenAI:AnalysisModel` and `OpenAI:ImageEditModel`. Images are sent to OpenAI only when you press one of those buttons.

## Price service

The service alternates Twelve Data and Finnhub every 15-minute polling cycle, falling back per missing symbol. It runs on weekdays from 9:30 a.m. through 4:00 p.m. Eastern. Provider-specific Canadian/OTC symbols can be entered during chart review.

To exercise the complete pipeline immediately without installing the Windows Service or waiting for a quarter-hour slot, run:

```powershell
dotnet run --project src\RiskReward.PriceService -- --run-once
```

The command honors the same guarded deployment target as the scheduled service. With the default Local target it writes `C:\RiskReward\PreviewSite\prices.json`, logs the primary/fallback provider results, and exits. It does not change the deployment target.

`--run-once` explicitly loads the project's .NET user-secrets even when the console environment is Production. An installed Windows Service normally runs under a different Windows identity and should receive its keys through protected service configuration or environment variables instead of developer user-secrets.

Install from an elevated PowerShell session with `scripts\install-price-service.ps1`. Remove it with `scripts\uninstall-price-service.ps1`.

## Calculation

Suggested allocation is logarithmic: `10 × (ln(upper) - ln(price)) / (ln(upper) - ln(lower))`, clamped to 0–10%. The geometric midpoint is therefore 5%.
