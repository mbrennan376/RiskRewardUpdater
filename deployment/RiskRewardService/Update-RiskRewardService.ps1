[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'RiskRewardPriceUpdater'
$installRoot = 'C:\Program Files\RiskRewardPriceService'
$serviceSource = Join-Path $PSScriptRoot 'service'
$runLog = Join-Path $PSScriptRoot 'Update-RiskRewardService-last-run.log'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

Start-Transcript -LiteralPath $runLog -Force | Out-Null
$exitCode = 0
try {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $service) { throw "Service $serviceName has not been installed. Run Install-RiskRewardService.cmd first." }
    if (-not (Test-Path -LiteralPath (Join-Path $serviceSource 'RiskReward.PriceService.exe'))) {
        throw "The updated service files are missing from $serviceSource."
    }

    Write-Host 'Stopping the Risk/Reward price service...' -ForegroundColor Cyan
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    Write-Host "Installing updated files in $installRoot..." -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $serviceSource '*') -Destination $installRoot -Recurse -Force

    Write-Host 'Starting the updated service...' -ForegroundColor Cyan
    Start-Service -Name $serviceName -ErrorAction Stop
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Write-Host 'The updated service is running. Existing credentials were preserved.' -ForegroundColor Green
    Write-Host 'Application log: C:\ProgramData\RiskReward\logs\price-service-YYYY-MM-DD.log'
    Write-Host 'Provider CSV:   C:\ProgramData\RiskReward\logs\provider-summary-YYYY-MM-DD.csv'
}
catch {
    $exitCode = 1
    Write-Error $_
}
finally {
    Stop-Transcript | Out-Null
}
exit $exitCode
