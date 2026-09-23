[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'RiskRewardPriceUpdater'
$installRoot = 'C:\Program Files\RiskRewardPriceService'
$stateRoot = 'C:\ProgramData\RiskReward'
$serviceSource = Join-Path $PSScriptRoot 'service'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

Write-Host ''
Write-Host 'Publishing LIVE prices with the corrected updater.' -ForegroundColor Cyan
Write-Host 'This does not upload HTML, CSS, JavaScript, data.json, or chart images.'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) { throw "Service $serviceName has not been installed. Run Install-RiskRewardService.cmd first." }
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Copy-Item -Path (Join-Path $serviceSource '*') -Destination $installRoot -Recurse -Force
$executable = Join-Path $installRoot 'RiskReward.PriceService.exe'

$serviceRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$storedEnvironment = (Get-ItemProperty -Path $serviceRegistry -Name Environment -ErrorAction Stop).Environment
foreach ($entry in $storedEnvironment) {
    $parts = $entry -split '=', 2
    if ($parts.Count -eq 2) { [Environment]::SetEnvironmentVariable($parts[0], $parts[1], 'Process') }
}

if ($env:RiskReward__AllowLivePublishing -ne 'true') { throw 'The installed service is not configured for guarded live publishing.' }
if ([string]::IsNullOrWhiteSpace($env:RiskReward__AzureStorageConnectionString)) { throw 'The installed service has no Azure Storage configuration.' }

New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $stateRoot 'deployment-target.json'), "{`r`n  `"target`": `"live`"`r`n}", [Text.UTF8Encoding]::new($false))

Push-Location $installRoot
try { & $executable --run-once }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "The corrected live price update failed with exit code $LASTEXITCODE. The service was not started." }

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host ''
Write-Host 'LIVE prices.json was published successfully.' -ForegroundColor Green
Write-Host "The $serviceName service is running." -ForegroundColor Green
Write-Host "Log: $stateRoot\logs\price-service-$([DateTime]::Now.ToString('yyyy-MM-dd')).log"
