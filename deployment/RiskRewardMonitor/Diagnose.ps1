$ErrorActionPreference = 'Continue'
$homeSeerRoot = 'C:\Program Files (x86)\HomeSeer HS4'
$pluginExe = Join-Path $homeSeerRoot 'HSPI_RiskRewardStatus.exe'
$pluginConfig = "$pluginExe.config"
$dependencyRoot = Join-Path $homeSeerRoot 'bin\RiskRewardStatus'
$reportPath = Join-Path $PSScriptRoot 'diagnostic-report.txt'

Start-Transcript -LiteralPath $reportPath -Force | Out-Null
try {
    Write-Output 'Risk/Reward HomeSeer Plugin Diagnostic'
    Write-Output ('Collected: {0:O}' -f [DateTimeOffset]::Now)
    Write-Output ('Computer: {0}' -f $env:COMPUTERNAME)
    Write-Output ('User: {0}' -f [Security.Principal.WindowsIdentity]::GetCurrent().Name)
    Write-Output ''

    Write-Output 'HomeSeer-related processes:'
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'HS4|HomeSeer' -or $_.ExecutablePath -match 'HomeSeer HS4' } |
        Select-Object Name, ProcessId, ExecutablePath, CommandLine | Format-List

    Write-Output 'HomeSeer-related Windows services:'
    Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'HS4|HomeSeer' -or $_.DisplayName -match 'HS4|HomeSeer' -or $_.PathName -match 'HomeSeer HS4' } |
        Select-Object Name, DisplayName, State, StartMode, PathName | Format-List

    Write-Output 'HomeSeer executable:'
    Get-Item -LiteralPath (Join-Path $homeSeerRoot 'HS4.exe') -ErrorAction SilentlyContinue |
        Select-Object FullName, Length, LastWriteTime, @{ Name = 'Version'; Expression = { $_.VersionInfo.FileVersion } } | Format-List

    Write-Output 'Plugin files:'
    @($pluginExe, $pluginConfig, (Join-Path $dependencyRoot 'PluginSdk.dll'),
        (Join-Path $dependencyRoot 'HSCF.dll'), (Join-Path $dependencyRoot 'Newtonsoft.Json.dll')) |
        ForEach-Object {
            if (Test-Path -LiteralPath $_) {
                $item = Get-Item -LiteralPath $_
                [pscustomobject]@{
                    Path = $item.FullName
                    Length = $item.Length
                    Version = $item.VersionInfo.FileVersion
                    Modified = $item.LastWriteTime
                    Blocked = [bool](Get-Item -LiteralPath $_ -Stream Zone.Identifier -ErrorAction SilentlyContinue)
                }
            }
            else {
                [pscustomobject]@{ Path = $_; Length = 'MISSING'; Version = ''; Modified = ''; Blocked = '' }
            }
        } | Format-List

    Write-Output 'Plugin assembly identity:'
    try { [Reflection.AssemblyName]::GetAssemblyName($pluginExe).FullName } catch { $_ | Format-List * -Force }

    Write-Output 'Plugin configuration:'
    if (Test-Path -LiteralPath $pluginConfig) { Get-Content -LiteralPath $pluginConfig }

    Write-Output 'Recent Windows application errors mentioning the plugin or HomeSeer:'
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddDays(-1) } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'RiskRewardStatus|HSPI_RiskRewardStatus|HomeSeer' } |
        Select-Object -First 30 TimeCreated, ProviderName, Id, LevelDisplayName, Message | Format-List

    Write-Output 'HomeSeer root HSPI files:'
    Get-ChildItem -LiteralPath $homeSeerRoot -Filter 'HSPI_*.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object Name | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
}
finally {
    Stop-Transcript | Out-Null
}
