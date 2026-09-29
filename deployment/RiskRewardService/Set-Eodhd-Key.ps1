[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'RiskRewardPriceUpdater'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

$key = (Read-Host 'Paste the EODHD API key and press Enter').Trim()
if ($key.Length -lt 8) { throw 'The EODHD API key appears incomplete.' }

$service = Get-Service -Name $serviceName -ErrorAction Stop
$serviceRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$environment = @((Get-ItemProperty -Path $serviceRegistry -Name Environment -ErrorAction Stop).Environment |
    Where-Object { $_ -notlike 'Providers__EodhdApiKey=*' })
$environment += "Providers__EodhdApiKey=$key"
New-ItemProperty -Path $serviceRegistry -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null

if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host 'EODHD is configured and the service has restarted for an immediate refresh.' -ForegroundColor Green
