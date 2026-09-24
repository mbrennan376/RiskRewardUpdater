# Risk/Reward Publisher

This solution contains a localhost chart-review site, shared chart/price infrastructure, and a Windows price-update service. The public dashboard remains a static site in `C:\source\repos\riskrewardsite`.

## Safe development workflow

1. Run `dotnet run --project RiskRewardUpdater.csproj` and open `http://127.0.0.1:5188`.
2. Review every changed screenshot. Enter numeric **upper** and **lower** lines; line color is intentionally ignored.
   - Choose whether that week's chart is USD or CAD. You can save a ticker/provider mapping for each currency (for example, `GKPRF` and `GSI.V`). Switching currency converts the saved upper and lower lines with the latest daily Bank of Canada USD/CAD rate.
   - Optionally ask OpenAI to suggest both values. Suggested values are copied into the inputs but the chart is returned to draft status for your review.
   - Optionally ask OpenAI to remove the lower-left presenter video box. The generated candidate is never selected automatically; compare it with the original and explicitly choose which image to publish.
3. Mark each screenshot ready or skipped, then publish. The default target is `C:\RiskReward\PreviewSite` and is available at `/preview/` from the admin app.
4. Verify the complete static site locally.
5. Configure `RiskReward:AzureStorageConnectionString` with .NET user-secrets. Live publishing is enabled by default; the target switch starts on Live Azure and can be changed to Local Preview immediately, without confirmation gates. A completion message is shown after publishing succeeds.

The publisher can deploy the current static-site assets and chart catalog even when no screenshots changed. Use **Edit an unchanged chart** to reopen any published chart with its saved upper/lower boundaries, change its metadata, mark it ready, and republish it without replacing the screenshot.

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

The service writes a daily application log and `provider-summary-YYYY-MM-DD.csv` under `RiskReward:StateFolder\logs`. Provider rows use `SuccessCount,FailureCount,Provider,DateTime`. Each update cycle removes both managed log types when they are more than 30 days old. Finnhub passes are capped at 45 seconds before the remaining symbols fall back to the alternate provider.

The normal final market-hours update begins at 4:00 PM Eastern. If it fails to publish fresh quotes for every chart, the service retries every five minutes through 4:30 PM; a service started during that recovery window attempts the missed close update immediately.

Every Windows Service start performs an immediate price refresh regardless of market hours. Outside market hours, Twelve Data's timestamp-free price response is labeled with the most recent weekday 4:00 PM Eastern close so the static site does not represent a closing price as a live observation.

Each fresh provider observation is also stored as compact static history under `history/manifest.json` and `history/{symbol}/yyyy-MM.json`. Provider timestamps are preferred; Twelve Data observations use the applicable 15-minute scheduler slot. Duplicate or older observations are not appended, and a history failure does not discard an otherwise valid `prices.json` update. The browser-side history loader returns available partial ranges and treats a missing manifest entry as a normal no-history state. No history chart is displayed yet.

The public site includes a right-side navigation drawer, About and Methodology pages, and the original Mark Gomes research link. The Methodology page distinguishes Mark's published buy-near-green/sell-near-red and 10-point guidance from this site's exact logarithmic interpolation formula.

`--run-once` explicitly loads the project's .NET user-secrets even when the console environment is Production. An installed Windows Service normally runs under a different Windows identity and should receive its keys through protected service configuration or environment variables instead of developer user-secrets.

Install from an elevated PowerShell session with `scripts\install-price-service.ps1`. Remove it with `scripts\uninstall-price-service.ps1`.

## Calculation

Suggested allocation is logarithmic: `10 × (ln(upper) - ln(price)) / (ln(upper) - ln(lower))`, clamped to 0–10%. The geometric midpoint is therefore 5%.
