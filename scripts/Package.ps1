param([ValidatePattern('^\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.-]+)?$')][string]$Version = '0.2.0')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Build.ps1')
& (Join-Path $PSScriptRoot 'Test.ps1')
$distRoot = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
$stageRoot = Join-Path $distRoot ('package-' + [Guid]::NewGuid().ToString('N'))
$appRoot = Join-Path $stageRoot 'app'
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'app') -File |
    Where-Object { $_.Name -match '^XiaomiPerformance\.(Tray|Cli|Core)\.(exe|dll|deps\.json|runtimeconfig\.json)$' } |
    Copy-Item -Destination $appRoot
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'XiaomiPerformance.Tray.exe'))) { throw 'Missing tray executable' }
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'XiaomiPerformance.Cli.exe'))) { throw 'Missing CLI executable' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'RELEASE_NOTES.md') -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'Start-Tray.ps1') -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'Enable-Autostart.ps1') -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'Disable-Autostart.ps1') -Destination $stageRoot
$archivePath = Join-Path $distRoot "xiaomi-performance-tray-$Version-win-x64.zip"
if (Test-Path -LiteralPath $archivePath) { throw "Archive already exists: $archivePath" }
Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $archivePath
$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath "$archivePath.sha256" -Encoding ascii
Write-Output $archivePath
