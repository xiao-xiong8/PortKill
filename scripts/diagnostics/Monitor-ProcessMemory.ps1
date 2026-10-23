param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [int]$Seconds = 300,
    [int]$IntervalSeconds = 5,
    [string]$OutputPath = 'process-memory.csv',
    [string]$Phase = 'observe'
)
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 1 -or $IntervalSeconds -lt 1) { throw '采样时长和间隔必须大于零。' }
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$first = $true
while ($timer.Elapsed.TotalSeconds -le $Seconds) {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (!$process) { break }
    $privateWorkingSet = $null
    try {
        $counter = Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess = $ProcessId"
        if ($counter) { $privateWorkingSet = $counter.WorkingSetPrivate }
    } catch { Write-Warning "专用工作集暂不可用：$($_.Exception.Message)" }
    $row = [pscustomobject]@{
        Timestamp = [DateTimeOffset]::Now.ToString('o')
        ElapsedSeconds = [math]::Round($timer.Elapsed.TotalSeconds, 3)
        Phase = $Phase
        Pid = $process.Id
        WorkingSetBytes = $process.WorkingSet64
        PrivateWorkingSetBytes = $privateWorkingSet
        PrivateBytes = $process.PrivateMemorySize64
        VirtualBytes = $process.VirtualMemorySize64
        Handles = $process.HandleCount
        Threads = $process.Threads.Count
        CpuSeconds = $process.CPU
    }
    if ($first) { $row | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding utf8; $first = $false }
    else { $row | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding utf8 -Append }
    $process.Dispose()
    Start-Sleep -Seconds $IntervalSeconds
}
