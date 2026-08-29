# Parallel runner for the Embedding test suite: one build, then N dotnet-test
# PROCESSES with disjoint class-prefix filters running concurrently.
#
# Process-level parallelism is the sanctioned way to parallelize this suite:
# the in-process xUnit parallelization is deliberately disabled
# (AssemblyInfo.cs - the engine's global AtomTable/FunctorTable are
# process-wide), but separate PROCESSES each get their own statics, so the
# constraint disappears by construction. Cross-process resources were audited:
# temp files are GUID-named (the two fixed dirs live in different classes, so
# disjoint class partitions never collide) and the DAP tests bind port 0
# (ephemeral).
#
# Usage:
#   powershell -File tests/test-embedding-parallel.ps1            # routine gate (Category!=Slow)
#   powershell -File tests/test-embedding-parallel.ps1 -Full      # pre-phase-close (includes Slow)
#
# The partitions are class-name-prefix buckets, hand-balanced from the
# per-class timing analysis (2026-07-27). Rebalance by moving prefixes if a
# bucket's wall time drifts far from the others (each log ends with its
# bucket's Duration).

param(
    [switch] $Full,
    # The suite is multi-targeted (netfx-target branch): without an explicit
    # framework a bare `dotnet test` would run BOTH flavors of every bucket.
    [string] $Framework = 'net10.0',
    # net48 only: 'x86' / 'x64' forces the testhost bitness (empty = default).
    [string] $Platform = '',
    # Debug by default, which is what a working tree is usually built as.
    # CI passes Release: that is the configuration that ships, and the IL
    # compiler emits DIFFERENT code in the two (the DbgCheck_* markers live
    # under `#if DEBUG`), so a Debug-only gate never sees the IL that runs.
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'tests/Shumway.Tests.Embedding'
$logDir = Join-Path $root 'TestResults/parallel'
New-Item -ItemType Directory -Force $logDir | Out-Null
Remove-Item (Join-Path $logDir '*.log') -Force -ErrorAction SilentlyContinue

# Disjoint partitions over FullyQualifiedName (class prefixes). The last
# bucket is the complement of the others, so every test runs exactly once.
# Balanced empirically (contended per-bucket durations printed per run): the
# debugger family (Adr035/036 - real-time waits, sockets, spawned processes)
# rides alone; the engine ADRs and chunk families pad the lighter buckets.
# Each partition expression is fully parenthesized BEFORE the Slow exclusion
# is AND-ed on ('&' binds tighter than '|' in vstest filters).
$parts = @(
    @{ Name = 'dbg35';   Expr = "(FullyQualifiedName~.Adr035)" },
    @{ Name = 'dbg36+';  Expr = "(FullyQualifiedName~.Adr036)|((FullyQualifiedName~.Adr0)&(FullyQualifiedName!~.Adr035))|(FullyQualifiedName~.Chunk1)" },
    @{ Name = 'ch23-ph'; Expr = "(FullyQualifiedName~.Chunk2)|(FullyQualifiedName~.Chunk3)|(FullyQualifiedName~.Phase)" },
    @{ Name = 'rest';    Expr = "(FullyQualifiedName!~.Adr0)&(FullyQualifiedName!~.Chunk1)&(FullyQualifiedName!~.Chunk2)&(FullyQualifiedName!~.Chunk3)&(FullyQualifiedName!~.Phase)" }
)
$buckets = foreach ($p in $parts) {
    $f = if ($Full) { $p.Expr } else { "(Category!=Slow)&($($p.Expr))" }
    @{ Name = $p.Name; Filter = $f }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()

# One build; the parallel runs are --no-build (concurrent builds of the same
# project would collide on the output tree).
# The net48 flavor only exists under the opt-in switch (Directory.Build.props).
$fxProps = @(); if ($Framework -eq 'net48') { $fxProps = @('-p:ShumwayNetFx=true') }

Write-Host "[parallel] building ($Configuration, $Framework)..."
dotnet build $proj -c $Configuration -f $Framework @fxProps --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host '[parallel] BUILD FAILED'; exit 1 }

Write-Host "[parallel] launching $($buckets.Count) test processes..."
$procs = @()
foreach ($b in $buckets) {
    $log = Join-Path $logDir "$($b.Name).log"
    $p = Start-Process -FilePath 'dotnet' -PassThru -NoNewWindow `
        -RedirectStandardOutput $log `
        -ArgumentList @(
            'test', $proj, '-c', $Configuration, '-f', $Framework, '--no-build', '--nologo'
            $fxProps
            '--filter', $b.Filter,
            '--blame-hang-timeout', '300s'
            if ($Platform -ne '') { '--', "RunConfiguration.TargetPlatform=$Platform" })
    # Cache the handle NOW: without this, .ExitCode reads $null after the
    # process exits (PS 5.1 Start-Process quirk) and $null -ne 0 is true.
    $null = $p.Handle
    $procs += @{ Bucket = $b.Name; Proc = $p; Log = $log }
}

$failed = $false
foreach ($e in $procs) {
    $e.Proc.WaitForExit()
    $tail = (Get-Content $e.Log | Select-String -Pattern 'Passed!|Failed!' | Select-Object -Last 1)
    if ($null -eq $tail) { $tail = "(no summary - see $($e.Log))" }
    Write-Host ("[parallel] {0,-8} {1}" -f $e.Bucket, $tail)
    # Failure = nonzero exit OR no clean Passed! summary (covers a crashed run
    # whose exit code was lost).
    if (($e.Proc.ExitCode -ne 0) -or ("$tail" -notmatch 'Passed!')) {
        $failed = $true
        # Surface the failing test names right here.
        Get-Content $e.Log | Select-String -Pattern '^\s*Failed ' |
            ForEach-Object { Write-Host ("[parallel]   {0}" -f $_.Line.Trim()) }
    }
}

$sw.Stop()
Write-Host ("[parallel] wall: {0:F0}s  (logs in {1})" -f $sw.Elapsed.TotalSeconds, $logDir)
if ($failed) { Write-Host '[parallel] RESULT: FAILED'; exit 1 }
Write-Host '[parallel] RESULT: PASSED'
exit 0
