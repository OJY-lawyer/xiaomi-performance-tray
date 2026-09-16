param()
$ErrorActionPreference = 'Stop'
$cliPath = Join-Path $PSScriptRoot 'XiaomiPerformance.Cli\bin\Release\net10.0-windows\XiaomiPerformance.Cli.exe'
$verifyDir = Join-Path $PSScriptRoot 'verification'
New-Item -ItemType Directory -Path $verifyDir -Force | Out-Null
$statePath = Join-Path $verifyDir ('simulation-' + [Guid]::NewGuid().ToString('N') + '.json')
$checks = [System.Collections.Generic.List[object]]::new()
function Check-Cli {
    param([string]$Name, [string[]]$Arguments, [int]$ExpectedExit, [string]$ExpectedCode, [string]$ExpectedMode)
    $json = & $cliPath @Arguments
    $actualExit = $LASTEXITCODE
    if ($actualExit -ne $ExpectedExit) { throw "$Name exit: $actualExit != $ExpectedExit" }
    $result = $json | ConvertFrom-Json
    if ($result.code -ne $ExpectedCode) { throw "$Name code: $($result.code) != $ExpectedCode" }
    if ($ExpectedMode -and $result.status.mode -ne $ExpectedMode) { throw "$Name mode: $($result.status.mode) != $ExpectedMode" }
    $checks.Add([pscustomobject]@{name=$Name; passed=$true; code=$result.code; mode=$result.status.mode; reading=$result.status.reading})
}
# Never call default SET or NEXT here: the native backend is now connected.
# This smoke suite is exclusively simulation and argument validation.
Check-Cli 'simulation initial smart' @('status','--simulate','--state-file',$statePath,'--json') 0 'ok' 'smart'
if (Test-Path -LiteralPath $statePath) { throw 'A simulation read unexpectedly created the file.' }
Check-Cli 'simulation set eco' @('set','eco','--simulate','--state-file',$statePath,'--json') 0 'ok' 'eco'
Check-Cli 'cycle eco to smart' @('next','--simulate','--state-file',$statePath,'--json') 0 'ok' 'smart'
Check-Cli 'cycle smart to turbo' @('next','--simulate','--state-file',$statePath,'--json') 0 'ok' 'turbo'
Check-Cli 'cycle turbo to eco' @('next','--simulate','--state-file',$statePath,'--json') 0 'ok' 'eco'
Check-Cli 'Chinese alias' @('set','极速','--simulate','--state-file',$statePath,'--json') 0 'ok' 'turbo'
[IO.File]::WriteAllText($statePath, 'invalid simulation data', [Text.UTF8Encoding]::new($false))
Check-Cli 'corrupt simulation state fails closed' @('next','--simulate','--state-file',$statePath,'--json') 6 'failed' ''
$before = [IO.File]::ReadAllText($statePath)
if ($before -ne 'invalid simulation data') { throw 'Corrupt state was unexpectedly overwritten.' }
$null = & $cliPath 'set' 'invalid' '--json' 2>&1
if ($LASTEXITCODE -ne 2) { throw 'Invalid mode accepted.' }
$checks.Add([pscustomobject]@{name='invalid mode rejected';passed=$true;code='argumentError';mode=$null;reading=$null})
$null = & $cliPath 'status' '--state-file' $statePath '--json' 2>&1
if ($LASTEXITCODE -ne 2) { throw 'Simulation state path accepted without --simulate.' }
$checks.Add([pscustomobject]@{name='state path requires simulation';passed=$true;code='argumentError';mode=$null;reading=$null})
$reportPath = Join-Path $verifyDir 'smoke-result.json'
[IO.File]::WriteAllText($reportPath, ($checks | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
# Delete only this run's explicit temporary simulation file under this script's folder.
$resolvedVerifyDir = [IO.Path]::GetFullPath($verifyDir).TrimEnd('\') + '\'
$resolvedState = [IO.Path]::GetFullPath($statePath)
if (-not $resolvedState.StartsWith($resolvedVerifyDir, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup path is outside verification.' }
Remove-Item -LiteralPath $resolvedState -Force
Write-Output ("PASS: {0} checks; {1}" -f $checks.Count, $reportPath)
