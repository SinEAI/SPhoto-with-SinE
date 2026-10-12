param([Parameter(Mandatory = $true)][string]$Exe)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$testRoot = Join-Path $env:RUNNER_TEMP 'sphoto-runtime-tests'
$results = Join-Path $root 'runtime-results'
New-Item -ItemType Directory -Force $testRoot, $results | Out-Null
$env:SPHOTO_NO_CRASH_DIALOG = '1'
$env:SPHOTO_DATA_DIR = Join-Path $testRoot 'profile'
[IO.File]::WriteAllText((Join-Path $results 'environment.json'), (@{
    source_commit = $env:GITHUB_SHA
    runner_os = $env:RUNNER_OS
    runner_arch = $env:RUNNER_ARCH
    cpu_count = [Environment]::ProcessorCount
    scope = 'Hosted Windows runner, generated fixtures, no physical GPU or power claim'
} | ConvertTo-Json))

$outcomes = @()
foreach ($test in @(
    @{ Name = 'runtime'; Mode = '--runtime-test'; File = 'runtime-results.json'; Timeout = 600 },
    @{ Name = 'stress-50000'; Mode = '--stress-test'; File = 'stress-50000.txt'; Timeout = 1500; Extra = '50000' },
    @{ Name = 'edit-return'; Mode = '--edit-return-test'; File = 'edit-return.txt'; Timeout = 300 }
)) {
    $log = Join-Path $testRoot $test.File
    $arguments = @($test.Mode, "`"$log`"")
    if ($test.Extra) { $arguments += $test.Extra }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -WorkingDirectory (Split-Path $Exe) -PassThru
    $timedOut = -not $process.WaitForExit($test.Timeout * 1000)
    if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
    $exists = Test-Path $log
    $complete = $false
    if ($exists) {
        if ($test.Name -eq 'runtime') {
            $report = Get-Content $log -Raw | ConvertFrom-Json
            $complete = ($report.source_commit -eq $env:GITHUB_SHA -and $report.passed -eq $true)
        } else {
            $complete = Select-String -Path $log -Pattern '^PASSED$' -Quiet
        }
        Copy-Item $log (Join-Path $results $test.File)
    }
    $passed = -not $timedOut -and $exists -and $complete -and $process.ExitCode -eq 0
    $outcomes += @{ name = $test.Name; passed = $passed; exit_code = $process.ExitCode; timed_out = $timedOut;
        seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 3); report_present = $exists }
    Write-Host "$($test.Name): passed=$passed exit=$($process.ExitCode) timed_out=$timedOut"
}
[IO.File]::WriteAllText((Join-Path $results 'outcomes.json'), (ConvertTo-Json -InputObject $outcomes -Depth 5))

# Check every selected report before anything is printed or uploaded. Databases,
# generated pictures, raw crash logs and stage captures remain in the temporary profile.
foreach ($file in Get-ChildItem $results -File) {
    python "$root/tools/check_release.py" --text-file $file.FullName
    if ($LASTEXITCODE) { throw 'runtime report privacy check failed; reports withheld' }
}
'shareable=true' >> $env:GITHUB_OUTPUT
@'
### Windows runtime tests

Generated-image tests execute the published build from this commit. The UI stress
test uses 50,000 files; those files contain repeated small images, not 50,000
unique high-resolution photographs. Rendering measurements use 12 MP and 24 MP
images separately. CPU/ANGLE timings on a hosted runner do not measure physical
GPU performance or power consumption. Failures are preserved in the report.
'@ >> $env:GITHUB_STEP_SUMMARY
foreach ($outcome in $outcomes) {
    "- $($outcome.name): passed=$($outcome.passed), exit=$($outcome.exit_code), $($outcome.seconds) s" >> $env:GITHUB_STEP_SUMMARY
}
if ($outcomes.Where({ -not $_.passed }).Count) { throw 'Windows runtime checks failed; see the privacy-checked reports' }
