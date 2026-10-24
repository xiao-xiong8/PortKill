#requires -Version 7.0
[CmdletBinding()]
param([string]$Repository)
$ErrorActionPreference = 'Stop'
if (-not $Repository) { $Repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path }
$projects = @{}
foreach ($file in Get-ChildItem (Join-Path $Repository 'src') -Recurse -Filter '*.csproj') {
    if ($file.FullName -match '[\\/](bin|obj)[\\/]') { continue }
    $projects[$file.FullName] = [xml][IO.File]::ReadAllText($file.FullName)
}
function Get-Closure([string]$entry) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending.Push([IO.Path]::GetFullPath((Join-Path $Repository $entry)))
    while ($pending.Count -gt 0) {
        $path = $pending.Pop()
        if (-not $seen.Add($path)) { continue }
        if (-not $projects.ContainsKey($path)) { throw "Unknown project: $path" }
        foreach ($reference in $projects[$path].SelectNodes('//ProjectReference')) {
            $pending.Push([IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $path) $reference.Include)))
        }
    }
    return @($seen)
}
$desktop = Get-Closure 'src/PortKiller.Desktop/PortKiller.Desktop.csproj'
foreach ($path in $desktop) {
    if ($path -match 'UpdateManagement|UpdateAdmin|UpdatePublisher') { throw 'Desktop references administration code' }
    foreach ($package in $projects[$path].SelectNodes('//PackageReference')) {
        if ($package.Include -match 'EntityFrameworkCore|Identity.EntityFrameworkCore') { throw 'Desktop references backend persistence' }
    }
}
foreach ($entry in @('src/Services/PortKiller.UpdateAdmin.Api/PortKiller.UpdateAdmin.Api.csproj','src/Services/PortKiller.UpdatePublisher.Worker/PortKiller.UpdatePublisher.Worker.csproj','src/Services/PortKiller.UpdateNotifier/PortKiller.UpdateNotifier.csproj','src/Tools/PortKiller.UpdateTool/PortKiller.UpdateTool.csproj')) {
    foreach ($path in (Get-Closure $entry)) {
        if ($path -match 'PortKiller.Desktop|Infrastructure.Windows|Modules.Ports|PortKiller.Updates[\\/]') { throw "Server references client/Windows code: $path" }
        foreach ($package in $projects[$path].SelectNodes('//PackageReference')) {
            if ($package.Include -match '^(Avalonia|CommunityToolkit.Mvvm)') { throw 'Server references desktop UI packages' }
        }
    }
}
$protocol = $projects[[IO.Path]::GetFullPath((Join-Path $Repository 'src/Modules/PortKiller.UpdateProtocol/PortKiller.UpdateProtocol.csproj'))]
if ($protocol.SelectNodes('//ProjectReference|//FrameworkReference').Count -ne 0) { throw 'Protocol must remain independent' }
foreach ($package in $protocol.SelectNodes('//PackageReference')) {
    if ($package.Include -match 'EntityFrameworkCore|Identity|Avalonia|AspNetCore') { throw 'Protocol references application frameworks' }
}
[xml]$commonProps = [IO.File]::ReadAllText((Join-Path $Repository 'Directory.Build.props'))
if ($commonProps.SelectNodes('//PackageReference|//ProjectReference|//FrameworkReference').Count -ne 0) { throw 'Root build properties must not introduce application dependencies' }
Write-Output "PASS architecture: $($projects.Count) production projects; desktop/server/protocol boundaries"
