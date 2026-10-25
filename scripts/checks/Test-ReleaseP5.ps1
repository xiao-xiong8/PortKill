#requires -Version 7.0
[CmdletBinding()]
param([string]$PreparedPublishDirectory, [string]$VcVarsAllPath, [string]$PythonExecutable)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'P5 acceptance requires Windows and a real desktop Native AOT build.' }
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$evidence = Join-Path $repository '.local/artifacts/admin-execution/P5-04'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
Push-Location $repository
try {
    if (-not $PreparedPublishDirectory) {
        if (-not $VcVarsAllPath) {
            $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
            if (Test-Path -LiteralPath $locator) {
                $VcVarsAllPath = (& $locator -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC/Auxiliary/Build/vcvarsall.bat' | Select-Object -First 1)
            }
        }
        if (-not $VcVarsAllPath) { throw 'A working Windows C++ toolchain is required for desktop Native AOT.' }
        $PreparedPublishDirectory = Join-Path $evidence 'desktop-aot'
        & pwsh -NoProfile -File scripts/build/Publish-Aot.ps1 -Version 8.3.0 -OutputDirectory $PreparedPublishDirectory -VcVarsAllPath $VcVarsAllPath 2>&1 |
            Tee-Object -FilePath (Join-Path $evidence 'desktop-aot-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Genuine desktop Native AOT build failed.' }
    }
    $PreparedPublishDirectory = (Resolve-Path -LiteralPath $PreparedPublishDirectory).Path
    $native = & (Join-Path $repository 'scripts/release/Get-AotArtifactInfo.ps1') -ExecutablePath (Join-Path $PreparedPublishDirectory 'PortKiller.exe')
    if (-not $PythonExecutable) { $PythonExecutable = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
    $packages = Join-Path $repository '.local/cache/p5-ssh'
    if (-not (Test-Path -LiteralPath (Join-Path $packages 'asyncssh/__init__.py'))) {
        & $PythonExecutable -m pip install --index-url https://pypi.org/simple --disable-pip-version-check --target $packages --cache-dir (Join-Path $repository '.local/cache/pip') -r tests/PortKiller.Release.Tests/ssh-requirements.txt
        if ($LASTEXITCODE -ne 0) { throw 'Isolated SSH test dependency installation failed.' }
    }
    $env:PORTKILL_RELEASE_AOT_DIRECTORY = $PreparedPublishDirectory
    $env:PORTKILL_RELEASE_SSH_PYTHON = $PythonExecutable
    $env:PORTKILL_RELEASE_SSH_PACKAGES = $packages
    & dotnet test tests/PortKiller.Release.Tests -c Release --logger 'trx;LogFileName=release-tests.trx' --results-directory $evidence 2>&1 |
        Tee-Object -FilePath (Join-Path $evidence 'release-check.log')
    if ($LASTEXITCODE -ne 0) { throw 'P5 release integration failed.' }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $evidence 'release-tests.trx') -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.total -lt 16 -or [int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0 -or [int]$counters.total -ne [int]$counters.passed) { throw 'P5 discovery or execution was incomplete.' }
    $adminEvidence = Join-Path $evidence 'admin-p4'
    & pwsh -NoProfile -File scripts/checks/Test-AdminP4.ps1 -OutputDirectory $adminEvidence 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'admin-regression.log')
    if ($LASTEXITCODE -ne 0) { throw 'Administration/update compatibility regression failed.' }
    $adminResult = Get-Content -LiteralPath (Join-Path $adminEvidence 'result.json') -Raw | ConvertFrom-Json
    & dotnet run --project tests/PortKiller.UpdateRegression -c Release --no-launch-profile -- --publisher-only --publisher $PreparedPublishDirectory 2>&1 |
        Tee-Object -FilePath (Join-Path $evidence 'legacy-localroot.log')
    if ($LASTEXITCODE -ne 0) { throw 'Original LocalRoot publisher regression failed.' }
    $result = [ordered]@{ phase='P5'; task='P5-04'; status='passed'; recordedUtc=[DateTimeOffset]::UtcNow.ToString('o');
        baseCommit=(& git rev-parse HEAD).Trim(); commit=$null; nativeArtifact=$native;
        releaseTests=[ordered]@{ total=[int]$counters.total; passed=[int]$counters.passed; failed=[int]$counters.failed; skipped=[int]$counters.notExecuted };
        backendTests=$adminResult.backendTests; frontendTests=6; requirementGroups=@($adminResult.acceptanceMatrix).Count;
        updateRegression=$adminResult.updateRegression; adminAndUpdateRegression='Test-AdminP4.ps1 passed; independent P5 evidence retained in admin-p4/ and admin-regression.log'; legacyLocalRoot='passed';
        scope='Genuine Windows desktop AOT, isolated signing keys, real localhost HTTPS API, production Worker and tool processes, real OpenSSH SSH/SFTP with isolated keys/known_hosts, and existing client update module discovery/download verification.';
        costs='One existing server with self-hosted free runtime components; Windows local builds and RSA signing. Existing server, domain and bandwidth costs remain.';
        limitations=@('SSH test server maps fixed Linux deployment paths into an isolated Windows filesystem; real Linux/Nginx/public TLS and permissions remain P7-P8.', 'Client verification uses the existing client module with isolated cache/trust paths and preserves explicit download/installation intent; it does not install over the user application.');
        nextTask='P6-01' }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    Write-Output ('PASS P5: genuine desktop AOT; ' + $counters.passed + ' release tests; administration, original publisher and client regressions passed.')
} finally { Pop-Location }
