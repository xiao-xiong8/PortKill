param(
    [string]$ExecutablePath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'src\PortKiller.Desktop\bin\Release\net10.0\PortKiller.exe')
)
$ErrorActionPreference = 'Stop'
$diagnosticExe = (Resolve-Path -LiteralPath $ExecutablePath).Path
if (Get-Process PortKiller -ErrorAction SilentlyContinue) {
    throw 'PortKiller 已在运行。请从托盘正常退出后再运行本脚本；命令行参数无法附加到旧实例。'
}
Start-Process -FilePath $diagnosticExe -ArgumentList '--memory-log' -WorkingDirectory (Split-Path -Parent $diagnosticExe) -WindowStyle Hidden
Write-Output '已启动临时内存日志：%LOCALAPPDATA%\PortKiller\Diagnostics\memory-日期-进程号.csv'
Write-Output '每 5 秒采样，最长 2 小时；不强制 GC。正常退出后停止，下次普通启动默认不记录。'
