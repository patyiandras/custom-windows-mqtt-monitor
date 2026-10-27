# Fordítás és telepítőcsomag a publish\ mappába (ezt kell átmásolni a cél-PC-re, ha nem ott fordítasz).
# Kell hozzá: .NET SDK (6+). A cél-PC-n nem kell semmi: a .NET Framework 4.8 minden Win10/11-en megvan.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'

dotnet build (Join-Path $root 'WinMqttMonitor.sln') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'A fordítás nem sikerült.' }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null
Copy-Item (Join-Path $root 'src\WinMqttMonitor\bin\Release\*') $out -Include *.exe, *.dll, *.config
Copy-Item (Join-Path $root 'src\WinMqttMonitor.Tray\bin\Release\*') $out -Include *.exe, *.config
Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'uninstall.ps1'), (Join-Path $root 'install.cmd'), (Join-Path $root 'uninstall.cmd'), (Join-Path $root 'config.example.json') $out
Write-Host "Kész: $out"
Get-ChildItem $out | Select-Object Name, Length | Format-Table -AutoSize
