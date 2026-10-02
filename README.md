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

Use **Chart ages** during a weekly call to see every current screenshot sorted oldest first. The call checklist labels screenshots under 14 days as Recent, 14–27 days as Consider asking, and 28 days or older as Request update. Search and age filters are available. A genuinely changed screenshot in the watched folder replaces the published date in this view immediately, while merely reopening an unchanged chart does not make it appear newer.

The shared deployment target is stored under `RiskReward:StateFolder`. The Windows Service reads that target before each update, so it also stays local until Live is deliberately enabled.

## Secrets

Do not commit API keys or Azure credentials. For local development:

```powershell
dotnet user-secrets set "RiskReward:AzureStorageConnectionString" "..." --project RiskRewardUpdater.csproj
dotnet user-secrets set "Providers:TwelveDataApiKey" "..." --project RiskRewardUpdater.csproj
dotnet user-secrets set "Providers:FinnhubApiKey" "..." --project RiskRewardUpdater.csproj
dotnet user-secrets set "Providers:EodhdApiKey" "..." --project RiskRewardUpdater.csproj
dotnet user-secrets set "OpenAI:ApiKey" "..." --project RiskRewardUpdater.csproj
```

Use equivalent environment variables or a protected service configuration for the Windows Service. Confirm that each market-data plan permits public display before enabling live price publication.

The OpenAI actions are entirely optional and disabled until an API key is configured. Line analysis uses the Responses API with a structured JSON result; video-box removal uses the Images edit API and saves the candidate under the local state folder. Model names can be changed with `OpenAI:AnalysisModel` and `OpenAI:ImageEditModel`. Images are sent to OpenAI only when you press one of those buttons.

## Price service

The service alternates Twelve Data and Finnhub every 15-minute polling cycle, falling back per missing symbol and then trying EODHD. It runs on weekdays from 9:30 a.m. through 4:00 p.m. Eastern. Provider-specific Canadian/OTC/EODHD symbols can be entered during chart review.

If every provider misses a CAD listing, the service can derive a CAD value from that chart's mapped USD listing using the latest Bank of Canada USD/CAD rate. Only a provider-supplied timestamp from the current applicable market session is accepted. Derived quotes are labeled in `prices.json` and stored under a separate `{CAD-SYMBOL}-DERIVED` history series so they never contaminate native Canadian observations.

Current quote records also retain `previousClose`, `dailyChange`, and `dailyChangePercent` when supplied by the provider. Twelve Data uses its `/quote` endpoint, while Finnhub and EODHD map their native daily-movement fields. A derived CAD fallback converts the previous close and absolute change with the same FX rate while retaining the percentage change. The public UI does not display these fields yet; they are being collected for the future ticker movement widget.

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

## Browser-local portfolio targets

The public dashboard’s holding popup stores a total invested amount, its USD or CAD portfolio currency, a global 1–10 point alert threshold, and each configured ticker’s held shares and allocation multiplier in browser `localStorage`. Multipliers run from 0.0× through 2.0× in 0.1 increments. Existing users’ `riskReward.totalInvestedCash` value is migrated into the versioned `riskReward.portfolio.v1` record the first time the updated dashboard loads, defaulting its currency to USD.

For each configured ticker:

- Expected investment is `total invested × suggested allocation ÷ 100`.
- Target holding value is `expected investment × multiplier`.
- Cross-listed Canadian companies let the user choose the exact US or Canadian symbol held. The chart quote is translated with the published USD/CAD rate before current value, target shares, and differences are calculated for that listing.
- The global alert threshold defaults to 3 allocation-scale points and can be selected from 1 through 10 points. Legacy percentage tolerances are divided by 10, rounded to the nearest point, clamped to 1–10, and persisted the first time they are loaded.
- The Actions required table includes a compact chart thumbnail that opens the existing full-screen chart viewer.
- Configured holdings show an `X% low`, `X% high`, or `On target` indicator in the main chart list, based on current holding value versus the multiplier-adjusted target.
- Target shares is `target holding value converted to the quote currency ÷ current quote`.
- Current allocation points equal `(current holding value / total invested / multiplier) × 100`. The left chart list shows the difference between suggested allocation points and current allocation points, colored green inside the selected threshold and red outside it. A nonzero holding against a 0.0× target is always outside the range.

`prices.json` schema version 2 publishes a dated `exchangeRates.USDCAD` record from the Bank of Canada. When a quote differs from the selected portfolio currency, target value is converted into quote currency before calculating shares, while current holding value is converted back into portfolio currency for Current / Target / Difference. If rate refresh fails, the previous published rate is retained with `isStale: true`; the browser pauses cross-currency target calculations until a fresh rate is available.

Each chart’s Set holding or Update holding button opens the local settings popup. Calculations require a usable current quote and configured holding; displayed share quantities are rounded, but target calculations use full precision.

Portfolio data is never sent to the server. **Export JSON** downloads all saved portfolio values, including portfolio currency, and **Import JSON** validates and replaces the current browser-local record after confirmation.
