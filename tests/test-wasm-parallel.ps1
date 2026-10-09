# Parallel runner for the Wasm test suite: one build, then N dotnet-test
# PROCESSES with disjoint class-letter filters running concurrently.
#
# In-process parallelism stays off (AssemblyInfo.cs: the engine's AtomTable /
# FunctorTable are process-wide); separate processes each get their own
# statics. Across processes the suite shares only GUID-named temp
# directories.
#
# Usage:
#   powershell -File tests/test-wasm-parallel.ps1                    # the gate
#   powershell -File tests/test-wasm-parallel.ps1 -Configuration Release -Diag -Filter "Category!=Slow"   # CI
#
# The buckets are first letters of the class name, balanced from per-class
# timings (2026-10-09: about 110 s of test time each). Rebalance by moving
# letters when a bucket's Duration drifts far from the others.

param(
    [string] $Configuration = 'Debug',
    # -p:ShumwayDiag=true: the [DiagFact] tests run instead of skipping.
    [switch] $Diag,
    # ANDed onto every bucket's filter, e.g. "Category!=Slow".
    [string] $Filter = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'tests/Shumway.Tests.Wasm'
$logDir = Join-Path $root 'TestResults/parallel-wasm'
New-Item -ItemType Directory -Force $logDir | Out-Null
Remove-Item (Join-Path $logDir '*.log') -Force -ErrorAction SilentlyContinue

function Letters([string] $l) {
    ($l.ToCharArray() | ForEach-Object { "(FullyQualifiedName~Wasm.$_)" }) -join '|'
}
function NotLetters([string] $l) {
    ($l.ToCharArray() | ForEach-Object { "(FullyQualifiedName!~Wasm.$_)" }) -join '&'
}
$parts = @(
    @{ Name = 'a-b';    Expr = Letters 'AB' },
    @{ Name = 'c-j';    Expr = Letters 'CDEFGJ' },
    @{ Name = 'i-r';    Expr = Letters 'ILMNOPQR' },
    # The complement of the others, so every test runs exactly once.
    @{ Name = 'rest';   Expr = NotLetters 'ABCDEFGJILMNOPQR' }
)
$buckets = foreach ($p in $parts) {
    $f = if ($Filter -ne '') { "($Filter)&($($p.Expr))" } else { $p.Expr }
    @{ Name = $p.Name; Filter = $f }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$diagProps = @(); if ($Diag) { $diagProps = @('-p:ShumwayDiag=true') }

Write-Host "[wasm] building ($Configuration)..."
dotnet build $proj -c $Configuration -f net10.0 @diagProps --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host '[wasm] BUILD FAILED'; exit 1 }

Write-Host "[wasm] launching $($buckets.Count) test processes..."
$procs = @()
foreach ($b in $buckets) {
    $log = Join-Path $logDir "$($b.Name).log"
    $errLog = Join-Path $logDir "$($b.Name).err.log"
    $p = Start-Process -FilePath 'dotnet' -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError $errLog `
        -ArgumentList @(
            'test', $proj, '-c', $Configuration, '-f', 'net10.0', '--no-build', '--nologo'
            $diagProps
            '--filter', $b.Filter,
            '--blame-hang-timeout', '300s')
    # Cache the handle NOW: without this, .ExitCode reads $null after the
    # process exits (PS 5.1 Start-Process quirk) and $null -ne 0 is true.
    $null = $p.Handle
    $procs += @{ Bucket = $b.Name; Proc = $p; Log = $log; ErrLog = $errLog }
}

$failed = $false
foreach ($e in $procs) {
    $e.Proc.WaitForExit()
    $tail = (Get-Content $e.Log | Select-String -Pattern 'Passed!|Failed!' | Select-Object -Last 1)
    if ($null -eq $tail) { $tail = "(no summary - see $($e.Log))" }
    Write-Host ("[wasm] {0,-5} {1}" -f $e.Bucket, $tail)
    if (($e.Proc.ExitCode -ne 0) -or ("$tail" -notmatch 'Passed!')) {
        $failed = $true
        Get-Content $e.Log | Select-String -Pattern '^\s*Failed ' |
            ForEach-Object { Write-Host ("[wasm]   {0}" -f $_.Line.Trim()) }
        # A host that dies after its summary says so only on stderr.
        if (Test-Path $e.ErrLog) {
            Get-Content $e.ErrLog | Select-String -Pattern 'Aborted|crashed|Fatal|Unhandled' |
                Select-Object -Last 5 | ForEach-Object { Write-Host ("[wasm]     {0}" -f $_.Line.Trim()) }
        }
    }
}

$sw.Stop()
Write-Host ("[wasm] wall: {0:F0}s  (logs in {1})" -f $sw.Elapsed.TotalSeconds, $logDir)
if ($failed) { Write-Host '[wasm] RESULT: FAILED'; exit 1 }
Write-Host '[wasm] RESULT: PASSED'
exit 0
