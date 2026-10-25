#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata
$aotPath = [IO.Path]::GetFullPath($ExecutablePath)
$aotStream = [IO.File]::OpenRead($aotPath)
$aotPe = [Reflection.PortableExecutable.PEReader]::new($aotStream)
try {
    if ($aotPe.HasMetadata -or $aotPe.PEHeaders.CoffHeader.Machine -ne [Reflection.PortableExecutable.Machine]::Amd64) {
        throw '要求 win-x64 Native AOT 可执行文件。'
    }
    $aotExport = $aotPe.PEHeaders.PEHeader.ExportTableDirectory
    if ($aotExport.Size -lt 40) { throw '可执行文件缺少 Native AOT 运行时导出。' }
    $aotExportBlock = $aotPe.GetSectionData($aotExport.RelativeVirtualAddress)
    [byte[]]$aotHeader = $aotExportBlock.GetContent(0, 40)
    $aotNameCount = [BitConverter]::ToUInt32($aotHeader, 24)
    $aotNameTable = [BitConverter]::ToUInt32($aotHeader, 32)
    if ($aotNameCount -lt 1 -or $aotNameCount -gt 4096) { throw 'Native AOT 导出表无效。' }
    $aotNameTableBlock = $aotPe.GetSectionData([int]$aotNameTable)
    $aotNameTableLength = [int]($aotNameCount * 4)
    [byte[]]$aotNames = $aotNameTableBlock.GetContent(0, $aotNameTableLength)
    $aotFound = $false
    for ($aotIndex = 0; $aotIndex -lt $aotNameCount; $aotIndex++) {
        $aotNameRva = [BitConverter]::ToInt32($aotNames, $aotIndex * 4)
        $aotBlock = $aotPe.GetSectionData($aotNameRva)
        [byte[]]$aotNameBytes = $aotBlock.GetContent(0, [Math]::Min(128, $aotBlock.Length))
        if ([Text.Encoding]::ASCII.GetString($aotNameBytes).Split([char]0)[0] -eq 'DotNetRuntimeDebugHeader') { $aotFound = $true; break }
    }
    if (-not $aotFound) { throw '可执行文件缺少 Native AOT 运行时导出；不能用普通 apphost 代替。' }
    [pscustomobject]@{ Runtime = 'win-x64'; NativeAot = $true; FileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($aotPath).FileVersion; ExeSha256 = (Get-FileHash -LiteralPath $aotPath -Algorithm SHA256).Hash }
} finally { $aotPe.Dispose(); $aotStream.Dispose() }
