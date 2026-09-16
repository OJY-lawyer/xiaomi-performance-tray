param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $repoRoot 'src'
$outputRoot = Join-Path $repoRoot 'app'
foreach ($project in @('XiaomiPerformance.Tray', 'XiaomiPerformance.Cli')) {
    $projectPath = Join-Path $sourceRoot "$project/$project.csproj"
    if (-not (Test-Path -LiteralPath $projectPath)) { throw "Source project not found: $projectPath" }
    dotnet publish $projectPath -c Release --no-self-contained -r win-x64 -o $outputRoot
    if ($LASTEXITCODE -ne 0) { throw "$project build failed" }
}
Write-Output "Built application: $outputRoot"
