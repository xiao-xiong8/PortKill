param(
    [Parameter(Mandatory=$true)][string]$InputPath,
    [string]$ExternalPath,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$memoryRows = @(Import-Csv -LiteralPath $InputPath)
if ($memoryRows.Count -lt 2) { throw '至少需要两条内存样本。' }
function Get-Number($row, [string]$field) {
    [double]::Parse($row.$field, [Globalization.CultureInfo]::InvariantCulture)
}
function Get-Stats($rows, [string]$field, [double]$scale = 1) {
    $values = @($rows | ForEach-Object { (Get-Number $_ $field) / $scale })
    $range = $values | Measure-Object -Minimum -Maximum
    [ordered]@{
        start = [math]::Round($values[0], 3)
        end = [math]::Round($values[-1], 3)
        min = [math]::Round($range.Minimum, 3)
        max = [math]::Round($range.Maximum, 3)
        delta = [math]::Round($values[-1] - $values[0], 3)
    }
}
$phaseRows = $memoryRows | ForEach-Object {
    $phase = if ((Get-Number $_ 'ElapsedSeconds') -lt 60) { 'warmup_first_60s' }
        elseif ($_.AutoRefresh -eq 'False') { 'scan_paused' }
        elseif ($_.WindowVisible -eq 'False') { 'hidden_auto_scan' }
        else { 'visible_auto_scan' }
    [pscustomobject]@{ Phase = $phase; Sample = $_ }
}
$phases = @($phaseRows | Group-Object Phase | ForEach-Object {
    $rows = @($_.Group.Sample)
    $elapsed = (Get-Number $rows[-1] 'ElapsedSeconds') - (Get-Number $rows[0] 'ElapsedSeconds')
    $allocation = (Get-Number $rows[-1] 'TotalAllocatedBytes') - (Get-Number $rows[0] 'TotalAllocatedBytes')
    [ordered]@{
        phase = $_.Name; samples = $rows.Count; first = $rows[0].Timestamp; last = $rows[-1].Timestamp
        seconds = [math]::Round($elapsed, 3)
        workingSetMiB = Get-Stats $rows 'WorkingSetBytes' 1MB
        privateMiB = Get-Stats $rows 'PrivateBytes' 1MB
        managedMiB = Get-Stats $rows 'ManagedBytes' 1MB
        gcHeapMiB = Get-Stats $rows 'LastGcHeapBytes' 1MB
        gcCommittedMiB = Get-Stats $rows 'GcCommittedBytes' 1MB
        allocatedMiBPerSecond = if ($elapsed -gt 0) { [math]::Round($allocation / 1MB / $elapsed, 4) } else { $null }
        completedScans = (Get-Number $rows[-1] 'CompletedScans') - (Get-Number $rows[0] 'CompletedScans')
        gen0 = (Get-Number $rows[-1] 'Gen0') - (Get-Number $rows[0] 'Gen0')
        gen1 = (Get-Number $rows[-1] 'Gen1') - (Get-Number $rows[0] 'Gen1')
        gen2 = (Get-Number $rows[-1] 'Gen2') - (Get-Number $rows[0] 'Gen2')
        handles = Get-Stats $rows 'Handles'; threads = Get-Stats $rows 'Threads'
        gdi = Get-Stats $rows 'GdiObjects'; user = Get-Stats $rows 'UserObjects'
        containers = Get-Stats $rows 'RetainedContainers'; rows = Get-Stats $rows 'SnapshotRows'
        inspectorQueries = Get-Stats $rows 'InspectorQueries'; inspectorPending = Get-Stats $rows 'InspectorPending'
    }
})
$result = [ordered]@{ input = [IO.Path]::GetFullPath($InputPath); samples = $memoryRows.Count; phases = $phases }
if ($ExternalPath) {
    $externalRows = @(Import-Csv -LiteralPath $ExternalPath | Where-Object { $_.PrivateWorkingSetBytes -ne '' })
    if ($externalRows.Count -gt 0) {
        $result.external = [ordered]@{
            input = [IO.Path]::GetFullPath($ExternalPath); samples = $externalRows.Count
            first = $externalRows[0].Timestamp; last = $externalRows[-1].Timestamp
            privateWorkingSetMiB = Get-Stats $externalRows 'PrivateWorkingSetBytes' 1MB
            privateMiB = Get-Stats $externalRows 'PrivateBytes' 1MB
        }
    }
}
$json = $result | ConvertTo-Json -Depth 8
if ($OutputPath) { $json | Set-Content -LiteralPath $OutputPath -Encoding utf8 }
$json
