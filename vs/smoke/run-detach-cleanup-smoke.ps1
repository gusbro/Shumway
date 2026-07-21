# ADR-035 smoke -- the user's report: stopped at a breakpoint, DETACH (or close VS), and
# the program's own output floods with "breakpoint hit ... stop" forever, because the
# session left the Break bytes armed and every later hit still ran the whole stop pipeline
# for a debugger that was gone.
#
#   D-1  after detach, the debuggee's stderr shows the ONE cleanup line
#        ("debugger detached: clearing breakpoints") -- the session noticed
#   D-2  and then NO flood: at most a handful of "breakpoint hit" lines appear after the
#        detach, not hundreds (the breakpoints were disarmed, the loop runs free)
#   D-3  the program RUNS ON (its normal output keeps coming / it finishes)
#
# Linked-exe shape (compile --debug + link --exe --debug, attach by hand) -- the user's
# ShumBlintDebug. Run from Windows PowerShell 5.1. ASCII only.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$work    = Join-Path $PSScriptRoot "detach-work"
$compile = Join-Path $PSScriptRoot "..\..\src\Shumway.Compile\bin/Release\net10.0\shumway-compile.exe"
$link    = Join-Path $PSScriptRoot "..\..\src\Shumway.Link\bin/Release\net10.0\shumway-link.exe"

foreach ($f in @($devenv, $compile, $link)) { if (-not (Test-Path $f)) { throw "missing $f" } }

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work | Out-Null
$pl = Join-Path $work "detachapp.pl"
# An INFINITE loop (repeat) so the process is still running when VS finishes launching and
# attaches -- and so "did it run on after detach?" is measured by stdout continuing to
# GROW, not by termination (the user's Blint keeps working too). It PRINTS periodically
# (reached(N)) and passes through tick/1 every N (where the breakpoint sits) -- so without
# the fix, after a detach every one of those hits runs the whole stop pipeline forever.
@'
:- public main/0.
main :-
    repeat,
    between(1, 100000, N),
    tick(N, _),
    fail.

tick(N, Doubled) :-
    Doubled is N * 2,
    ( 0 =:= N mod 20000 -> report(N) ; true ).

report(N) :- write(reached(N)), nl.
'@ | Out-File -FilePath $pl -Encoding ascii

$lines = Get-Content $pl
$bpLine = 0
for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match 'Doubled is N \* 2') { $bpLine = $i + 1 } }
Write-Host "breakpoint line: $bpLine -> $($lines[$bpLine - 1].Trim())"

Write-Host "[0/6] compile --debug + link --exe --debug ..."
& $compile --debug $pl -o (Join-Path $work "detachapp.shmo")
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
& $link (Join-Path $work "detachapp.shmo") --goal main --exe (Join-Path $work "detachapp") --debug
if ($LASTEXITCODE -ne 0) { throw "link failed" }
$exe = Join-Path $work "detachapp.exe"

$stderrLog = Join-Path $env:TEMP "shumway-detach-stderr.log"
$stdoutLog = Join-Path $env:TEMP "shumway-detach-stdout.log"
Remove-Item $stderrLog, $stdoutLog -ErrorAction SilentlyContinue

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class RotFinderDt
{
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int r, out IRunningObjectTable p);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int r, out IBindCtx p);
    public static object FindDte(int pid)
    {
        IRunningObjectTable rot; GetRunningObjectTable(0, out rot);
        IEnumMoniker e; rot.EnumRunning(out e);
        IMoniker[] m = new IMoniker[1];
        string suffix = ":" + pid;
        while (e.Next(1, m, IntPtr.Zero) == 0)
        {
            IBindCtx bc; CreateBindCtx(0, out bc);
            string name; m[0].GetDisplayName(bc, null, out name);
            if (name.StartsWith("!VisualStudio.DTE.") && name.EndsWith(suffix))
            { object dte; rot.GetObject(m[0], out dte); return dte; }
        }
        return null;
    }
}
'@

function Invoke-WithRetry([scriptblock]$Action, [int]$Attempts = 30, [int]$DelayMs = 2000) {
    for ($i = 1; $i -le $Attempts; $i++) {
        try { return & $Action } catch {
            if ($i -eq $Attempts) { throw }
            Start-Sleep -Milliseconds $DelayMs
        }
    }
}

$dbgProc = $null; $vsProc = $null
$results = [ordered]@{}

try {
    Write-Host "[1/6] starting detachapp.exe ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $dbgProc = Start-Process -FilePath $exe -PassThru -NoNewWindow `
        -RedirectStandardError $stderrLog -RedirectStandardOutput $stdoutLog
    Write-Host "  pid $($dbgProc.Id)"

    Write-Host "[2/6] starting devenv /rootsuffix Exp + attach ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    Remove-Item Env:\SHUMWAY_DEBUG_DIAG
    $dte = Invoke-WithRetry { $d = [RotFinderDt]::FindDte($vsProc.Id); if ($null -eq $d) { throw "no DTE yet" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10
    $target = Invoke-WithRetry {
        $p = @($dte.Debugger.LocalProcesses) | Where-Object { $_.ProcessID -eq $dbgProc.Id }
        if (-not $p) { throw "debuggee not in LocalProcesses yet" }
        $p
    } 30 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Start-Sleep -Seconds 5

    function Select-PrologThread {
        Invoke-WithRetry {
            $chosen = $null
            foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
                $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
                $hit = @($names | Where-Object {
                    $_ -match '^\w+/\d+$' -or $_ -match '!\d+$' -or $_ -match 'BytecodeInterpreter|PrologEngine' `
                        -or $_ -match '^\[Shumway'
                })
                if ($hit.Count -gt 0) { $chosen = $t; break }
            }
            if (-not $chosen) { throw "no thread with Prolog on it yet" }
            $dte.Debugger.CurrentThread = $chosen
            $chosen
        } 10 2000
        Start-Sleep -Seconds 2
    }
    function Wait-ForBreak([int]$Seconds = 30) {
        for ($i = 0; $i -lt $Seconds; $i++) {
            if ((Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000) -eq 2) { return $true }
            Start-Sleep -Seconds 1
        }
        return $false
    }

    Write-Host "[3/6] Break All twice (module bootstrap) ..."
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Start-Sleep -Seconds 4
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null

    $bpFile = $null
    try { $doc = $dte.ActiveDocument; if ($doc -and $doc.FullName -match 'detachapp\.pl$') { $bpFile = $doc.FullName } } catch { }
    if (-not $bpFile) {
        $mat = Get-ChildItem (Join-Path $env:TEMP "shumway-debug\*\detachapp.pl") -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($mat) { $bpFile = $mat.FullName }
    }
    if (-not $bpFile) { $bpFile = $pl }
    Write-Host "  breakpoint file: $bpFile"

    Write-Host "[4/6] breakpoint at line $bpLine; F5; stop there ..."
    Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $bpFile, $bpLine) | Out-Null } 10 2000
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    $stopped = Wait-ForBreak 30
    Write-Host "  stopped at the breakpoint: $stopped"
    $results["D-0 stopped at the breakpoint"] = $stopped

    # How much stderr existed at the moment of detach -- the baseline the flood is measured
    # against. And how far stdout had progressed (report(N) lines).
    $stderrBefore = if (Test-Path $stderrLog) { (Get-Content $stderrLog).Count } else { 0 }
    $stdoutBefore = if (Test-Path $stdoutLog) { (Get-Content $stdoutLog) -join "`n" } else { "" }

    Write-Host "[5/6] DETACH (leave the process running) ..."
    Invoke-WithRetry { $dte.Debugger.DetachAll() } 15 2000
    # Let the freed program run to completion (20000 iterations is a blink without the
    # per-hit pipeline; WITH the bug it crawls and floods).
    Start-Sleep -Seconds 12

    Write-Host "[6/6] reading the debuggee's own output ..."
    $stderrAfterLines = if (Test-Path $stderrLog) { Get-Content $stderrLog } else { @() }
    $cleanupLines = @($stderrAfterLines | Where-Object { $_ -match "debugger detached: clearing breakpoints" })
    # "breakpoint hit ... stop" lines that appeared AFTER the detach baseline.
    $hitLinesTotal = @($stderrAfterLines | Where-Object { $_ -match "breakpoint hit .* stop" })
    $hitLinesAfter = [Math]::Max(0, $hitLinesTotal.Count - 0)  # all such lines are post-arm; report raw
    $stdoutAfter = if (Test-Path $stdoutLog) { (Get-Content $stdoutLog) -join "`n" } else { "" }
    $ranOn = ($stdoutAfter.Length -gt $stdoutBefore.Length) -or ($stdoutAfter -match "done")

    Write-Host "  cleanup line present: $($cleanupLines.Count -ge 1)"
    Write-Host "  total 'breakpoint hit' lines in stderr: $($hitLinesTotal.Count)"
    Write-Host "  stdout grew after detach / saw 'done': $ranOn"
    Write-Host "  --- stdout tail ---"
    ($stdoutAfter -split "`n") | Select-Object -Last 6 | ForEach-Object { Write-Host "    $_" }

    $results["D-1 the session noticed the detach (cleanup line)"] = ($cleanupLines.Count -ge 1)
    # The fix caps the flood: without it, one hit per iteration after detach (~thousands);
    # with it, a small bounded number before the disarm takes hold.
    $results["D-2 no flood after detach (< 50 hit lines total)"] = ($hitLinesTotal.Count -lt 50)
    $results["D-3 the program ran on after detach"] = $ranOn
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($dbgProc -and -not $dbgProc.HasExited) { $dbgProc.Kill() } } catch {}
}

Start-Sleep -Seconds 2
Write-Host ""
Write-Host "=== engine stderr (tail) ==="
if (Test-Path $stderrLog) { Get-Content $stderrLog | Select-Object -Last 10 | ForEach-Object { Write-Host "  $_" } }

Write-Host ""
Write-Host "--- results ---"
$allOk = $true
foreach ($key in $results.Keys) {
    $ok = $results[$key]
    if (-not $ok) { $allOk = $false }
    Write-Host ("{0,-52} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
}
Write-Host ""
if ($allOk) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL" }
