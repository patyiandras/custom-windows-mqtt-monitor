#Requires -RunAsAdministrator
# Telepítés / frissítés (rendszergazdai PowerShellből):  .\install.ps1
#  - a binárisok: C:\Program Files\WinMqttMonitor
#  - beállítás, napló, IPC: C:\ProgramData\WinMqttMonitor   (a config.json-t csak SYSTEM és a rendszergazdák olvassák)
#  - Windows-szolgáltatás: WinMqttMonitor (automatikus indulás, hiba esetén újraindul)
#  - tálcás segéd: minden bejelentkezéskor indul (HKLM\...\Run)
#  - a régi „TCP Listener service”-t és a ShutdownDaemon automatikus indítását kivezeti (a 7268-as
#    SmartThings-végpontot az új szolgáltatás viszi tovább, amíg a configban be van kapcsolva)
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'WinMqttMonitor')
)
$ErrorActionPreference = 'Stop'
$service = 'WinMqttMonitor'
$dataDir = Join-Path $env:ProgramData 'WinMqttMonitor'
$config = Join-Path $dataDir 'config.json'
$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

# --- Forrás: a szkript mappája (publish-csomag) vagy a repó publish\ mappája; ha nincs, fordít ---
$src = $PSScriptRoot
if (-not (Test-Path (Join-Path $src 'WinMqttMonitor.exe'))) {
    $src = Join-Path $PSScriptRoot 'publish'
    if (-not (Test-Path (Join-Path $src 'WinMqttMonitor.exe'))) { & (Join-Path $PSScriptRoot 'build.ps1') }
}

# --- Beállítás ---
New-Item -ItemType Directory -Force (Join-Path $dataDir 'ipc'), (Join-Path $dataDir 'logs') | Out-Null
if (-not (Test-Path $config)) {
    Copy-Item (Join-Path $src 'config.example.json') $config
    icacls $config /inheritance:r /grant:r 'SYSTEM:F' '*S-1-5-32-544:F' | Out-Null
    Write-Host "Létrehozva: $config" -ForegroundColor Yellow
    Write-Host 'Írd bele az MQTT-jelszót (és ha kell, a gép nevét), majd futtasd újra az install.cmd-t.' -ForegroundColor Yellow
    notepad $config
    exit 1
}
if ((Get-Content $config -Raw) -match 'IDE_A_JELSZO') {
    Write-Host "A $config még a minta-jelszót tartalmazza — írd át, és futtasd újra." -ForegroundColor Yellow
    exit 1
}
icacls $config /inheritance:r /grant:r 'SYSTEM:F' '*S-1-5-32-544:F' | Out-Null
# Az ipc mappába a tálcás segéd (sima felhasználó) is ír: Users = módosítás.
icacls (Join-Path $dataDir 'ipc') /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null

# --- A régi megoldás kivezetése ---
$old = Get-Service -Name 'TCP Listener service' -ErrorAction SilentlyContinue
if ($old) {
    Write-Host 'Régi „TCP Listener service” leállítása és törlése...'
    if ($old.Status -ne 'Stopped') { Stop-Service $old.Name -Force -ErrorAction SilentlyContinue }
    sc.exe delete "TCP Listener service" | Out-Null
}
Get-Process -Name 'ShutdownDaemon', 'TcpListener' -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($key in @($runKey, 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run')) {
    $props = Get-ItemProperty $key -ErrorAction SilentlyContinue
    if (-not $props) { continue }
    foreach ($p in $props.PSObject.Properties) {
        if ("$($p.Value)" -match 'ShutdownDaemon') {
            Write-Host "Régi automatikus indítás törölve: $key\$($p.Name)"
            Remove-ItemProperty $key -Name $p.Name
        }
    }
}
$shell = New-Object -ComObject WScript.Shell
$startupDirs = @("$env:ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp") +
    (Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup' })
foreach ($dir in $startupDirs) {
    Get-ChildItem $dir -Filter *.lnk -ErrorAction SilentlyContinue | ForEach-Object {
        if ($shell.CreateShortcut($_.FullName).TargetPath -match 'ShutdownDaemon') {
            Write-Host "Régi indítóparancsikon törölve: $($_.FullName)"
            Remove-Item $_.FullName -Force
        }
    }
}
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { ($_.Actions.Execute -join ' ') -match 'ShutdownDaemon|TcpListener' } |
    ForEach-Object { Write-Host "Figyelem: régi ütemezett feladat maradt: $($_.TaskPath)$($_.TaskName) — töröld kézzel, ha már nem kell." -ForegroundColor Yellow }

# --- Binárisok ---
$svc = Get-Service -Name $service -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service $service -Force }
Get-Process -Name 'WinMqttMonitorTray' -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $src '*') $InstallDir -Include *.exe, *.dll, *.config -Force
$exe = Join-Path $InstallDir 'WinMqttMonitor.exe'
$tray = Join-Path $InstallDir 'WinMqttMonitorTray.exe'

# --- Szolgáltatás ---
if (-not $svc) {
    New-Service -Name $service -BinaryPathName "`"$exe`"" -DisplayName 'Windows MQTT Monitor (Home Assistant)' `
        -Description 'A gép állapotát MQTT-n küldi a Home Assistantnak, és fogadja a leállítás/zárolás parancsokat. github.com/patyiandras/custom-windows-mqtt-monitor' `
        -StartupType Automatic | Out-Null
}
sc.exe failure $service reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

# --- Tűzfal a régi SmartThings-végponthoz ---
$cfgJson = Get-Content $config -Raw | ConvertFrom-Json
$legacyOn = -not ($cfgJson.legacy_http -and $cfgJson.legacy_http.enabled -eq $false)
$legacyPort = if ($cfgJson.legacy_http -and $cfgJson.legacy_http.port) { $cfgJson.legacy_http.port } else { 7268 }
Get-NetFirewallRule -Name 'WinMqttMonitor-Legacy' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($legacyOn) {
    New-NetFirewallRule -Name 'WinMqttMonitor-Legacy' -DisplayName "WinMqttMonitor – SmartThings PC Control ($legacyPort)" `
        -Direction Inbound -Protocol TCP -LocalPort $legacyPort -Action Allow -Profile Private, Domain | Out-Null
}

# --- Tálcás segéd ---
Set-ItemProperty $runKey -Name 'WinMqttMonitorTray' -Value "`"$tray`""

Start-Service $service
# Nem emelt jogú indítás a jelenlegi felhasználónak (az explorer a saját jogaival indítja).
Start-Process explorer.exe -ArgumentList "`"$tray`""
Start-Sleep -Seconds 3
Get-Service $service | Format-Table Name, Status, StartType -AutoSize
Write-Host "Napló: $dataDir\logs\monitor.log"
Get-Content (Join-Path $dataDir 'logs\monitor.log') -Tail 5 -ErrorAction SilentlyContinue
