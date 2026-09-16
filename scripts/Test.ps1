param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $repoRoot 'src'
$testOutput = Join-Path $repoRoot 'artifacts/tests'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
dotnet run --project (Join-Path $sourceRoot 'XiaomiPerformance.Tests/XiaomiPerformance.Tests.csproj') -c Release -- $testOutput
if ($LASTEXITCODE -ne 0) { throw 'C# simulation tests failed' }
dotnet build (Join-Path $sourceRoot 'XiaomiPerformance.Cli/XiaomiPerformance.Cli.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }
& (Join-Path $sourceRoot 'Smoke-Test.ps1')
