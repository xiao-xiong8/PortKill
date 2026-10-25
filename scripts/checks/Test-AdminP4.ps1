#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$evidence = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repository '.local/artifacts/admin-execution/P4-06' }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
Push-Location $repository
try {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-Admin.ps1') 2>&1 |
        Tee-Object -FilePath (Join-Path $evidence 'admin-check.log')
    if ($LASTEXITCODE -ne 0) { throw 'P4 administration checks failed.' }
    $trxPath = Join-Path $evidence 'admin-tests.trx'
    Copy-Item -LiteralPath (Join-Path $repository '.local/artifacts/admin-tests/latest/admin-tests.trx') -Destination $trxPath
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $matrix = Get-Content -LiteralPath (Join-Path $repository 'tests/PortKiller.UpdateAdmin.Tests/P4AcceptanceMatrix.json') -Raw | ConvertFrom-Json
    if ([int]$counters.total -lt $matrix.minimumBackendTests -or [int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0 -or
        [int]$counters.total -ne [int]$counters.passed) { throw 'P4 test discovery or execution is incomplete.' }
    $coverage = foreach ($requirement in $matrix.requirements) {
        $results = foreach ($prefix in $requirement.tests) {
            $matchedCases = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName.Contains($prefix, [StringComparison]::Ordinal) })
            if ($matchedCases.Count -eq 0 -or @($matchedCases | Where-Object outcome -ne 'Passed').Count -gt 0) {
                throw ('P4 acceptance coverage is missing or failed: ' + $prefix)
            }
            foreach ($test in $matchedCases) { [ordered]@{ name = $test.testName; outcome = $test.outcome; duration = $test.duration } }
        }
        [ordered]@{ id = $requirement.id; requirement = $requirement.requirement; passed = $true; tests = @($results) }
    }
    $regressionLog = Join-Path $evidence 'update-regression.log'
    & dotnet run --project tests/PortKiller.UpdateRegression -c Release --no-launch-profile 2>&1 |
        Tee-Object -FilePath $regressionLog
    if ($LASTEXITCODE -ne 0) { throw 'Existing update regression failed.' }
    $regressionText = Get-Content -LiteralPath $regressionLog -Raw
    $regressionCount = [regex]::Matches($regressionText, '(?m)^PASS ').Count
    if ($regressionCount -lt $matrix.minimumUpdateRegressionCases) { throw 'Existing update regression discovery is incomplete.' }
    $result = [ordered]@{
        task = 'P4-06'; phase = 'P4'; status = 'passed'; summaryType = 'execution-summary'
        recordedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        baseCommit = (& git rev-parse HEAD).Trim(); commit = $null
        command = 'pwsh -NoProfile -File scripts/checks/Test-AdminP4.ps1'
        backendTests = [ordered]@{ total = [int]$counters.total; passed = [int]$counters.passed; failed = [int]$counters.failed; skipped = [int]$counters.notExecuted; trx = 'admin-tests.trx' }
        productionWorkerIntegrationCases = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName.Contains('P4ProcessIntegrationTests.') }).Count
        frontendChecks = 'Six unit tests, API types, lint, type checking and production build passed in admin-check.log.'
        architectureAndOpenApi = 'Passed in admin-check.log.'
        acceptanceMatrix = @($coverage)
        updateRegression = [ordered]@{ passed = $regressionCount; exitCode = 0; log = 'update-regression.log' }
        limitations = @(
            'Local acceptance uses isolated databases, test signing keys, loopback HTTPS with temporary certificates, production Worker processes and tool child processes.'
            'Commit-point faults and oversized output are injected only by the test executable, which calls the production ReleaseStore; production executables have no fault switches.'
            'After deliberately killing a Worker, tests expire its isolated database lease to simulate elapsed time rather than wait a full wall-clock minute.'
            'Real Linux/Nginx/production HTTPS/public Internet acceptance remains P7-P8; complete SSH script compatibility and real desktop AOT artifact acceptance remain P5.'
        )
        nextTask = 'P5-01'
    }
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    Write-Output ('PASS P4 acceptance: ' + $counters.passed + ' backend tests, ' + $coverage.Count + ' requirement groups, ' + $regressionCount + ' update regression cases.')
} finally { Pop-Location }
