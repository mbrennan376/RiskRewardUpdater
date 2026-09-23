[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'RiskRewardPriceUpdater'
$displayName = 'Risk/Reward Price Updater'
$installRoot = 'C:\Program Files\RiskRewardPriceService'
$stateRoot = 'C:\ProgramData\RiskReward'
$serviceSource = Join-Path $PSScriptRoot 'service'
$siteSource = Join-Path $PSScriptRoot 'site'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

function Read-RequiredValue([string]$Label, [int]$MinimumLength) {
    $plain = (Read-Host $Label).Trim()
    if ([string]::IsNullOrWhiteSpace($plain)) { throw "$Label is required." }
    if ($plain.Length -lt $MinimumLength) { throw "$Label contained only $($plain.Length) characters. Paste the complete value and rerun the installer." }
    Write-Host "Accepted $($plain.Length) characters." -ForegroundColor DarkGray
    return $plain
}

Write-Host ''
Write-Host 'Risk/Reward LIVE price-service installer' -ForegroundColor Cyan
Write-Host 'This installs a Windows Service and immediately writes prices.json to the public Azure static site.'
Write-Host 'Credentials are stored in the protected Windows Service registry configuration, not on the shared drive.'
Write-Host ''
$confirmation = Read-Host 'Type PUBLISH LIVE to continue'
if ($confirmation -cne 'PUBLISH LIVE') {
    Write-Host 'Cancelled. Nothing was installed or published.' -ForegroundColor Yellow
    exit 2
}

$twelveDataKey = Read-RequiredValue 'Twelve Data API key (paste and press Enter)' 16
$finnhubKey = Read-RequiredValue 'Finnhub API key (paste and press Enter)' 20
$storageAccountName = (Read-Host 'Azure Storage account name').Trim()
if ($storageAccountName -cnotmatch '^[a-z0-9]{3,24}$') {
    throw 'Enter only the Azure Storage account name: 3-24 lowercase letters and numbers, without a URL.'
}
$storageAccountKey = Read-RequiredValue 'Azure Storage account access key - Key1 or Key2 (paste and press Enter)' 40
$azureConnection = "DefaultEndpointsProtocol=https;AccountName=$storageAccountName;AccountKey=$storageAccountKey;EndpointSuffix=core.windows.net"
$containerName = Read-Host 'Azure static-site container name [$web]'
if ([string]::IsNullOrWhiteSpace($containerName)) { $containerName = '$web' }

if (-not (Test-Path -LiteralPath (Join-Path $serviceSource 'RiskReward.PriceService.exe'))) {
    throw "The packaged service executable is missing from $serviceSource."
}
if (-not (Test-Path -LiteralPath (Join-Path $siteSource 'index.html'))) {
    throw "The packaged static site is missing from $siteSource."
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host 'Stopping the existing service...'
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
Write-Host "Copying service files to $installRoot..."
Copy-Item -Path (Join-Path $serviceSource '*') -Destination $installRoot -Recurse -Force
$installedSite = Join-Path $installRoot 'site'
New-Item -ItemType Directory -Path $installedSite -Force | Out-Null
Copy-Item -Path (Join-Path $siteSource '*') -Destination $installedSite -Recurse -Force

$executable = Join-Path $installRoot 'RiskReward.PriceService.exe'
if ($existing) {
    & sc.exe config $serviceName binPath= "`"$executable`"" start= auto | Out-Null
} else {
    New-Service -Name $serviceName -BinaryPathName "`"$executable`"" -DisplayName $displayName -StartupType Automatic | Out-Null
}

$serviceEnvironment = @(
    "Providers__TwelveDataApiKey=$twelveDataKey",
    "Providers__FinnhubApiKey=$finnhubKey",
    'RiskReward__AllowLivePublishing=true',
    "RiskReward__AzureStorageConnectionString=$azureConnection",
    "RiskReward__LiveContainerName=$containerName",
    "RiskReward__StateFolder=$stateRoot"
)
$serviceRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
New-ItemProperty -Path $serviceRegistry -Name Environment -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

$targetFile = Join-Path $stateRoot 'deployment-target.json'
[IO.File]::WriteAllText($targetFile, "{`r`n  `"target`": `"live`"`r`n}", [Text.UTF8Encoding]::new($false))

# The one-time process needs the same configuration that Service Control Manager will supply later.
$env:Providers__TwelveDataApiKey = $twelveDataKey
$env:Providers__FinnhubApiKey = $finnhubKey
$env:RiskReward__AllowLivePublishing = 'true'
$env:RiskReward__AzureStorageConnectionString = $azureConnection
$env:RiskReward__LiveContainerName = $containerName
$env:RiskReward__StateFolder = $stateRoot

Write-Host 'Publishing the static UI and running the initial LIVE price update...' -ForegroundColor Cyan
Push-Location $installRoot
try { & $executable --deploy-site $installedSite --run-once }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "The initial live update failed with exit code $LASTEXITCODE. The service was not started." }

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
$installed = Get-Service -Name $serviceName
Write-Host ''
Write-Host "Service: $($installed.DisplayName)" -ForegroundColor Green
Write-Host "Status:  $($installed.Status)" -ForegroundColor Green
Write-Host 'The static UI and initial live prices.json update completed.' -ForegroundColor Green
