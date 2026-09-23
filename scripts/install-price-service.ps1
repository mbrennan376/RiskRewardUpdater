param(
  [string]$PublishPath = "$PSScriptRoot\..\artifacts\price-service",
  [string]$ServiceName = "RiskRewardPriceUpdater"
)
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\RiskReward.PriceService\RiskReward.PriceService.csproj"
dotnet publish $project -c Release -r win-x64 --self-contained true -o $PublishPath
$exe = Join-Path $PublishPath "RiskReward.PriceService.exe"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
  Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
  sc.exe config $ServiceName binPath= "`"$exe`""
} else {
  New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "Risk/Reward Price Updater" -StartupType Automatic
}
Start-Service -Name $ServiceName
Get-Service -Name $ServiceName
