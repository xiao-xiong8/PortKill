#requires -Version 7.0
[CmdletBinding()]
param([string]$FontDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw '此脚本只用于 Windows 当前用户字体安装。' }
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not $FontDirectory) { $FontDirectory = Join-Path $projectRoot 'artifacts/font-install' }
$fontNames = @('NotoSansSC-Regular', 'NotoSansSC-Medium', 'NotoSansSC-SemiBold', 'Inter-Medium', 'Inter-SemiBold')
foreach ($name in $fontNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $FontDirectory "$name.ttf"))) { throw "缺少静态字体 $name.ttf，请先构建字体。" }
}
$fontTargetRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft/Windows/Fonts'
[IO.Directory]::CreateDirectory($fontTargetRoot) | Out-Null
$fontRegistry = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Fonts'
New-Item -Path $fontRegistry -Force | Out-Null
if (-not ('PortKillerFontRegistration' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PortKillerFontRegistration {
    [DllImport("gdi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern int AddFontResourceEx(string path, uint flags, IntPtr reserved);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}
'@
}
foreach ($name in $fontNames) {
    $source = (Resolve-Path -LiteralPath (Join-Path $FontDirectory "$name.ttf")).Path
    $target = Join-Path $fontTargetRoot "PortKiller-$name.ttf"
    if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $source -Destination $target }
    elseif ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
        throw "当前用户已有不同版本的 $target，停止覆盖。"
    }
    $loaded = [PortKillerFontRegistration]::AddFontResourceEx($target, 0, [IntPtr]::Zero)
    if ($loaded -le 0) { throw "Windows 加载字体失败：$name" }
    New-ItemProperty -Path $fontRegistry -Name "PortKiller $name (TrueType)" -Value $target -PropertyType String -Force | Out-Null
    Write-Output "已安装 $name"
}
$fontMessageResult = [UIntPtr]::Zero
[void][PortKillerFontRegistration]::SendMessageTimeout([IntPtr]0xffff, 0x001D, [UIntPtr]::Zero,
    [IntPtr]::Zero, 2, 1000, [ref]$fontMessageResult)
Write-Output '当前用户字体安装完成。软件自身使用内嵌字体，其他已打开的软件可能需要重启以刷新字体列表。'
