$ErrorActionPreference = 'Stop'
if (Get-ScheduledTask -TaskName 'OJY.XiaomiPerformance.Tray' -ErrorAction SilentlyContinue) {
    Disable-ScheduledTask -TaskName 'OJY.XiaomiPerformance.Tray' | Out-Null
}
Write-Output '已关闭下次登录自启；当前运行的托盘可从右键菜单退出。'
