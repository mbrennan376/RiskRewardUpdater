RISK/REWARD PRICE SERVICE - SERVER INSTALL PACKAGE
==================================================

Purpose
-------
Installs the Risk/Reward price updater as an automatic Windows Service,
publishes the packaged HTML/CSS/JavaScript to the LIVE Azure static site, and
performs one immediate update of prices.json.

The deployment preserves live data.json, prices.json until the new price cycle,
and everything under charts/. It does not replace chart metadata or images.

Install
-------
1. Log on to the server.
2. Open D:\Public\RiskRewardService.
3. Double-click Install-RiskRewardService.cmd.
4. Approve the Windows administrator prompt.
5. Type PUBLISH LIVE when prompted.
6. Paste each complete API key directly at its prompt and press Enter.
7. Enter the Azure Storage account name, then paste one storage account access
   key (Key1 or Key2) directly at its prompt and press Enter. The installer
   constructs the connection string; do not use a URL or full connection string.

The package copies the executable to:
  C:\Program Files\RiskRewardPriceService

The packaged service is self-contained; .NET does not need to be installed
separately on the server.

Service state is stored at:
  C:\ProgramData\RiskReward

The service name is:
  RiskRewardPriceUpdater

Daily updater logs are written to:
  C:\ProgramData\RiskReward\logs\price-service-YYYY-MM-DD.log

Daily provider monitoring summaries are written as CSV to:
  C:\ProgramData\RiskReward\logs\provider-summary-YYYY-MM-DD.csv

Both log types are automatically deleted after 30 days. Scheduled cycles now
log when they start and finish. A Finnhub cycle is capped at 45 seconds so a
failing provider cannot silently consume most of a 15-minute update interval.
If the 4:00 PM update does not publish fresh prices for every chart, the service
retries every five minutes through 4:30 PM. Starting the service during that
window also triggers the missed final-close update immediately.
Every service start also performs one immediate refresh regardless of the time.
After-hours Twelve Data prices are timestamped at the latest weekday 4:00 PM
close instead of being presented as live observations.

Update an existing installation
-------------------------------
After replacing this package with a newer copy, double-click:
  Update-RiskRewardService.cmd

This stops the service, installs the new binaries, and restarts it without
asking for or changing the credentials already stored for the service. Its
complete output is saved beside the package as:
  Update-RiskRewardService-last-run.log

The service also reports errors to Windows Event Viewer under Windows Logs >
Application. Provider request URLs are suppressed so API keys are not logged.

Security
--------
Credentials are not stored in this shared package. The installer places them
in the administrator-protected Windows Service registry configuration. The
service logs suppress provider request URLs so API keys are not printed.

Local development site on the laptop
------------------------------------
Rendered preview directory:
  C:\RiskReward\PreviewSite

Chart catalog:
  C:\RiskReward\PreviewSite\data.json

Current local prices:
  C:\RiskReward\PreviewSite\prices.json

Editable static-site source:
  C:\source\repos\riskrewardsite
