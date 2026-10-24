#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('linux-x64','linux-arm64','win-x64')][string]$Runtime='linux-x64',[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $repository "artifacts/update-server/$Runtime" }
foreach ($component in @(@{Name='tool';Project='Tools/PortKiller.UpdateTool'},@{Name='notifier';Project='Services/PortKiller.UpdateNotifier'})) {
    & dotnet publish (Join-Path $repository "src/$($component.Project)") -c Release -r $Runtime --self-contained true -o (Join-Path $OutputDirectory $component.Name) --nologo
    if ($LASTEXITCODE -ne 0) { throw "服务端发布失败：$($component.Name)" }
}
Write-Output "服务端自包含产物：$OutputDirectory"
