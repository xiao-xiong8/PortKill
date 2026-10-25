#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$VcVarsAllPath
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$evidence = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $repository '.local/artifacts/admin-execution/P8-03'
}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

Push-Location $repository
try {
    Assert-Condition $IsWindows 'P8-03 requires Windows for a genuine desktop Native AOT build and execution.'
    if (-not $VcVarsAllPath) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $VcVarsAllPath = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC/Auxiliary/Build/vcvarsall.bat' | Select-Object -First 1)
        }
    }
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($VcVarsAllPath) -and (Test-Path -LiteralPath $VcVarsAllPath)) 'A working Visual Studio C++ toolchain is required for P8-03.'

    $pipelineDirectory = Join-Path $evidence 'update-pipeline'
    & pwsh -NoProfile -File (Join-Path $repository 'scripts/build/Test-UpdatePipeline.ps1') -VcVarsAllPath $VcVarsAllPath -SkipPerformance -Clean -OutputDirectory $pipelineDirectory 2>&1 |
        Tee-Object -FilePath (Join-Path $evidence 'desktop-compatibility.log')
    if ($LASTEXITCODE -ne 0) { throw 'The desktop update compatibility pipeline failed.' }

    $pipelineResultPath = Join-Path $pipelineDirectory 'result.json'
    Assert-Condition (Test-Path -LiteralPath $pipelineResultPath) 'The desktop compatibility pipeline did not produce result.json.'
    $pipelineResult = Get-Content -LiteralPath $pipelineResultPath -Raw | ConvertFrom-Json
    Assert-Condition ([bool]$pipelineResult.Passed) 'The desktop compatibility pipeline did not report success.'

    # The nested pipeline transcript omits native child output; the wrapper log retains every marker.
    $logPath = Join-Path $evidence 'desktop-compatibility.log'
    Assert-Condition (Test-Path -LiteralPath $logPath) 'The desktop compatibility pipeline log is missing.'
    $log = Get-Content -LiteralPath $logPath -Raw
    $matrixPath = Join-Path $repository 'tests/PortKiller.UpdateRegression/P8DesktopCompatibilityMatrix.json'
    $matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
    $coverage = foreach ($group in @($matrix.requiredMarkers)) {
        $matches = foreach ($marker in @($group.markers)) {
            Assert-Condition ($log.Contains([string]$marker, [StringComparison]::Ordinal)) ("P8-03 compatibility marker missing: $marker")
            [ordered]@{ marker = [string]$marker; passed = $true }
        }
        [ordered]@{ id = [string]$group.id; requirement = [string]$group.requirement; passed = $true; markers = @($matches) }
    }

    $aotDirectory = Join-Path $pipelineDirectory 'desktop-aot'
    $aotInfoObject = & (Join-Path $repository 'scripts/release/Get-AotArtifactInfo.ps1') -ExecutablePath (Join-Path $aotDirectory 'PortKiller.exe')
    Assert-Condition ([bool]$aotInfoObject.NativeAot -and $aotInfoObject.Runtime -eq 'win-x64') 'The P8-03 desktop artifact is not a genuine win-x64 Native AOT executable.'

    $result = [ordered]@{
        task = 'P8-03'
        phase = 'P8'
        status = 'passed'
        summaryType = 'desktop-compatibility-and-native-aot-regression'
        recordedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        machine = [Environment]::MachineName
        command = 'pwsh -NoProfile -File scripts/checks/Test-DesktopCompatibility.ps1'
        toolchain = [ordered]@{ vcVarsAll = (Resolve-Path -LiteralPath $VcVarsAllPath).Path; runtime = 'win-x64'; nativeAot = $true }
        artifact = $aotInfoObject
        pipeline = $pipelineResult
        matrix = @($coverage)
        evidence = @('desktop-compatibility.log', 'update-pipeline/pipeline.log', 'update-pipeline/result.json', 'update-pipeline/desktop-aot/PortKiller.exe')
        limitations = @(
            '本次证据在 Windows 本机、隔离临时目录、测试签名材料和本地回环服务执行。',
            'AOT 更新宿主使用真实 Native AOT 可执行文件和依赖文件，但不会覆盖用户实际安装目录。',
            '公网、Linux/Nginx/生产 TLS 和正式代码签名证书仍属于 P7/P8 的专用环境验收范围。'
        )
        nextTask = 'P8-04'
    }
    $result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    Write-Output ('PASS P8-03 desktop compatibility: ' + $coverage.Count + ' compatibility groups and genuine win-x64 Native AOT regression passed.')
} finally {
    Pop-Location
}
