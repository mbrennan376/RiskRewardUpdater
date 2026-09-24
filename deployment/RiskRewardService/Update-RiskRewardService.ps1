[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'RiskRewardPriceUpdater'
$installRoot = 'C:\Program Files\RiskRewardPriceService'
$stateRoot = 'C:\ProgramData\RiskReward'
$serviceSource = Join-Path $PSScriptRoot 'service'
$siteSource = Join-Path $PSScriptRoot 'site'
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
    if (-not (Test-Path -LiteralPath (Join-Path $siteSource 'index.html'))) {
        throw "The updated static-site files are missing from $siteSource."
    }

    Write-Host 'Stopping the Risk/Reward price service...' -ForegroundColor Cyan
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    Write-Host "Installing updated files in $installRoot..." -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $serviceSource '*') -Destination $installRoot -Recurse -Force

    $installedSite = Join-Path $installRoot 'site'
    New-Item -ItemType Directory -Path $installedSite -Force | Out-Null
    Copy-Item -Path (Join-Path $siteSource '*') -Destination $installedSite -Recurse -Force

    $serviceRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $storedEnvironment = (Get-ItemProperty -Path $serviceRegistry -Name Environment -ErrorAction Stop).Environment
    foreach ($entry in $storedEnvironment) {
        $parts = $entry -split '=', 2
        if ($parts.Count -eq 2) { [Environment]::SetEnvironmentVariable($parts[0], $parts[1], 'Process') }
    }
    if ($env:RiskReward__AllowLivePublishing -ne 'true') { throw 'The installed service is not configured for live publishing.' }
    if ([string]::IsNullOrWhiteSpace($env:RiskReward__AzureStorageConnectionString)) { throw 'The installed service has no Azure Storage configuration.' }
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $stateRoot 'deployment-target.json'), "{`r`n  `"target`": `"live`"`r`n}", [Text.UTF8Encoding]::new($false))

    Write-Host 'Publishing the updated static site and refreshing prices now...' -ForegroundColor Cyan
    $executable = Join-Path $installRoot 'RiskReward.PriceService.exe'
    Push-Location $installRoot
    try { & $executable --deploy-site $installedSite --run-once }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "The live deployment/update failed with exit code $LASTEXITCODE. The service was not restarted." }

    Write-Host 'Starting the updated service...' -ForegroundColor Cyan
    Start-Service -Name $serviceName -ErrorAction Stop
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Write-Host 'The updated site and fresh prices were published, and the service is running. Existing credentials were preserved.' -ForegroundColor Green
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
