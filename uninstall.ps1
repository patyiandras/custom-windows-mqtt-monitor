#Requires -RunAsAdministrator
# Eltávolítás: .\uninstall.ps1          (a HA-ból is törli az eszközt; a beállítás megmarad)
#              .\uninstall.ps1 -Purge   (a C:\ProgramData\WinMqttMonitor mappát is törli)
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'WinMqttMonitor'),
    [switch]$Purge
)
$ErrorActionPreference = 'Continue'
$service = 'WinMqttMonitor'
$dataDir = Join-Path $env:ProgramData 'WinMqttMonitor'
$exe = Join-Path $InstallDir 'WinMqttMonitor.exe'

if (Get-Service -Name $service -ErrorAction SilentlyContinue) {
    Stop-Service $service -Force -ErrorAction SilentlyContinue
    if (Test-Path $exe) { & $exe --remove }   # HA-eszköz törlése (üres discovery-üzenetek)
    sc.exe delete $service | Out-Null
}
Get-Process -Name 'WinMqttMonitorTray' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'WinMqttMonitorTray' -ErrorAction SilentlyContinue
Get-NetFirewallRule -Name 'WinMqttMonitor-Legacy' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Start-Sleep -Seconds 1
Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
if ($Purge) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'Eltávolítva.'
