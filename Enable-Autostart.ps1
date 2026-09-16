$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'app\XiaomiPerformance.Tray.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw '未找到托盘程序。' }
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $exe -Argument '--autostart' -WorkingDirectory (Split-Path -LiteralPath $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'OJY.XiaomiPerformance.Tray' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description '当前用户登录后启动小米性能托盘，启动只读取状态。' -Force | Out-Null
Start-ScheduledTask -TaskName 'OJY.XiaomiPerformance.Tray'
Write-Output '已开启当前用户登录自启。'
