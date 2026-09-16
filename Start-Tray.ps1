$ErrorActionPreference = 'Stop'
$trayPath = Join-Path $PSScriptRoot 'app/XiaomiPerformance.Tray.exe'
if (-not (Test-Path -LiteralPath $trayPath)) { throw 'Tray executable not found. Extract the full release archive first.' }
Start-Process -FilePath $trayPath -WorkingDirectory (Split-Path $trayPath -Parent) -WindowStyle Hidden
