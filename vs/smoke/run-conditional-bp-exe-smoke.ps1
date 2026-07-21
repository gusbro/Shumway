# ADR-035 D5 smoke check -- CONDITIONAL breakpoints against the LINKED --exe --debug shape.
#
# The user's deployment (ShumBlintDebug): shumway-compile --debug + shumway-link --exe
# --debug, run by hand, attached by hand. Their session log showed the residual defect this
# smoke exists to catch: every notify's snapshot read back EMPTY (seq=0 running=True
# frames=0) and the component auto-continued -- the conditional breakpoint never stopped.
#
#   E5-1  the condition reaches the engine (engine stderr shows per-hit fails/holds)
#   E5-2  execution stops where the condition holds (N mod 500 == 0 in Locals)
#   E5-3  the snapshots read at stops are REAL (seq > 0), not the empty-channel pattern
#
# Run from Windows PowerShell 5.1. ASCII only.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$work    = Join-Path $PSScriptRoot "condbp-exe-work"
$compile = Join-Path $PSScriptRoot "..\..\src\Shumway.Compile\bin/Release\net10.0\shumway-compile.exe"
$link    = Join-Path $PSScriptRoot "..\..\src\Shumway.Link\bin/Release\net10.0\shumway-link.exe"

foreach ($f in @($devenv, $compile, $link)) { if (-not (Test-Path $f)) { throw "missing $f" } }

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work | Out-Null
$pl = Join-Path $work "loopapp.pl"
@'
:- public main/0.
main :-
    repeat,
    between(1, 100000, N),
    tick(N, _),
    fail.

tick(N, Doubled) :-
    Doubled is N * 2.
'@ | Out-File -FilePath $pl -Encoding ascii

# The breakpoint goes on tick/2's body -- the last non-empty line.
$lines = Get-Content $pl
$bpLine = 0
for ($i = $lines.Count - 1; $i -ge 0; $i--) {
    if ($lines[$i].Trim().Length -gt 0) { $bpLine = $i + 1; break }
}
$condition = "0 =:= N mod 500"
Write-Host "breakpoint line in loopapp.pl: $bpLine  ->  $($lines[$bpLine - 1].Trim())"
Write-Host "condition: $condition"

Write-Host "[0/6] compile --debug + link --exe --debug ..."
& $compile --debug $pl -o (Join-Path $work "loopapp.shmo")
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
& $link (Join-Path $work "loopapp.shmo") --goal main --exe (Join-Path $work "loopapp") --debug
if ($LASTEXITCODE -ne 0) { throw "link failed" }
$exe = Join-Path $work "loopapp.exe"
if (-not (Test-Path $exe)) { throw "no exe produced" }

# NOTE: the breakpoint FILE is the materialized debug source the debugger serves for this
# exe, not the .pl we compiled from -- the engine reports the module list through the
# channel, and the component's module path is what F9 must target. The base exe smoke
# learns it from the channel file next-file hint; here we take it from the component log
# after attach. Fallback: the original .pl (a non-materialized engine binds that too).

$stderrLog = Join-Path $env:TEMP "shumway-condbp-exe-stderr.log"
$stdoutLog = Join-Path $env:TEMP "shumway-condbp-exe-stdout.log"
Remove-Item $stderrLog, $stdoutLog -ErrorAction SilentlyContinue

$componentLog = Join-Path $env:TEMP "shumway-debug\component.log"
$componentStart = 0
if (Test-Path $componentLog) { $componentStart = (Get-Item $componentLog).Length }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class RotFinderCbx
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
    Write-Host "[1/6] starting loopapp.exe (stderr -> $stderrLog) ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $dbgProc = Start-Process -FilePath $exe -PassThru -NoNewWindow `
        -RedirectStandardError $stderrLog -RedirectStandardOutput $stdoutLog
    Write-Host "  pid $($dbgProc.Id)"

    Write-Host "[2/6] starting devenv /rootsuffix Exp ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    Remove-Item Env:\SHUMWAY_DEBUG_DIAG
    $dte = Invoke-WithRetry { $d = [RotFinderCbx]::FindDte($vsProc.Id); if ($null -eq $d) { throw "no DTE yet" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    Write-Host "[3/6] attaching to loopapp.exe ..."
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
            if ($dte.Debugger.CurrentThread.ID -ne $chosen.ID) { throw "CurrentThread did not take" }
            $chosen
        } 10 2000
        Start-Sleep -Seconds 2
    }

    function Wait-ForBreak([int]$Seconds = 30) {
        for ($i = 0; $i -lt $Seconds; $i++) {
            $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
            if ($m -eq 2) { return $true }
            Start-Sleep -Seconds 1
        }
        return $false
    }

    function Get-TickN {
        try {
            $plFrame = @($dte.Debugger.CurrentThread.StackFrames) |
                Where-Object { $_.FunctionName -match '(^|:)tick[(/!]' } | Select-Object -First 1
            if (-not $plFrame) { return -1 }
            $dte.Debugger.CurrentStackFrame = $plFrame
            Start-Sleep -Seconds 1
            $n = @($dte.Debugger.CurrentStackFrame.Locals) |
                Where-Object { $_.Name -eq "N" } | Select-Object -First 1
            if ($null -eq $n) { return -1 }
            return [long]$n.Value
        } catch { return -1 }
    }

    Write-Host "[4/6] Break All twice (module bootstrap) ..."
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Start-Sleep -Seconds 4
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null

    # The single-file exe serves a MATERIALIZED copy of the source; F9 must target that
    # file (the module VS knows). Read the module path from the component log's
    # "breakpoint enabled"/module lines -- or, more simply, from the frames' document.
    # DTE gives it straight: the current (Prolog) stack frame's enclosing document.
    $bpFile = $pl
    try {
        $doc = $dte.ActiveDocument
        if ($doc -and $doc.FullName -match 'loopapp\.pl$') { $bpFile = $doc.FullName }
    } catch { }
    Write-Host "  breakpoint file: $bpFile"

    Write-Host "[5/6] setting the CONDITIONAL breakpoint at ${bpFile}:${bpLine} ..."
    $bpAdded = $true
    try {
        Invoke-WithRetry {
            $dte.Debugger.Breakpoints.Add("", $bpFile, $bpLine, 1, $condition, 1) | Out-Null
        } 10 2000
    }
    catch { $bpAdded = $false; Write-Host "  Breakpoints.Add threw: $($_.Exception.Message)" }
    $results["E5-0 conditional Breakpoints.Add succeeds"] = $bpAdded

    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Write-Host "  waiting for the conditional stop ..."
    $stopped = Wait-ForBreak 90
    Write-Host "  stopped: $stopped"

    $n1 = -1
    if ($stopped) {
        Select-PrologThread | Out-Null
        $frames = Invoke-WithRetry {
            $f = @($dte.Debugger.CurrentThread.StackFrames) | ForEach-Object { $_.FunctionName }
            if (-not $f) { throw "no frames yet" }
            $f
        } 10 2000
        Write-Host "  frames: $($frames -join ' | ')"
        $n1 = Get-TickN
        Write-Host "  N at stop #1: $n1"
    }
    $results["E5-2 stops only where the condition holds"] =
        ($stopped -and $n1 -gt 0 -and ($n1 % 500) -eq 0)

    Write-Host "[6/6] F5 -> the NEXT conditional stop ..."
    $n2 = -1
    if ($stopped) {
        Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
        if (Wait-ForBreak 90) {
            Select-PrologThread | Out-Null
            $n2 = Get-TickN
            Write-Host "  N at stop #2: $n2"
        }
    }
    $results["E5-3 stops again, later, condition holding"] =
        ($n2 -gt $n1 -and ($n2 % 500) -eq 0)
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($dbgProc -and -not $dbgProc.HasExited) { $dbgProc.Kill() } } catch {}
}

Start-Sleep -Seconds 2

$stderr = @()
if (Test-Path $stderrLog) { $stderr = Get-Content $stderrLog }
$fails  = @($stderr | Where-Object { $_ -match "breakpoint condition .* fails \(run on\)" })
$holds  = @($stderr | Where-Object { $_ -match "breakpoint condition .* holds \(stop\)" })
$noCond = @($stderr | Where-Object { $_ -match "no condition attached" })
Write-Host ""
Write-Host "=== engine stderr (tail) ==="
$stderr | Select-Object -Last 8 | ForEach-Object { Write-Host "  $_" }
Write-Host "  [counts] fails=$($fails.Count) holds=$($holds.Count) no-condition=$($noCond.Count)"
$results["E5-1 the condition reached the engine"] =
    ($fails.Count -gt 0 -and $holds.Count -ge 1 -and $noCond.Count -eq 0)

$emptySnaps = @(); $realSnaps = @()
if (Test-Path $componentLog) {
    $stream = [System.IO.File]::Open($componentLog, 'Open', 'Read', 'ReadWrite')
    try {
        $stream.Position = $componentStart
        $reader = New-Object System.IO.StreamReader($stream)
        $newLog = $reader.ReadToEnd() -split "`r?`n"
    } finally { $stream.Dispose() }
    $emptySnaps = @($newLog | Where-Object { $_ -match "snapshot: \w+ seq=0 " })
    $realSnaps  = @($newLog | Where-Object { $_ -match "snapshot: \w+ seq=[1-9]" })
    Write-Host ""
    Write-Host "=== component.log (this run): snapshots ==="
    $newLog | Where-Object { $_ -match "snapshot:|condition parsed|channel found" } |
        Select-Object -Last 10 | ForEach-Object { Write-Host "  $_" }
}
Write-Host "  [counts] empty-snapshots=$($emptySnaps.Count) real-snapshots=$($realSnaps.Count)"
$results["E5-4 snapshots read at stops are real (no seq=0)"] =
    ($realSnaps.Count -ge 1 -and $emptySnaps.Count -eq 0)

Write-Host ""
Write-Host "--- results ---"
$allOk = $true
foreach ($key in $results.Keys) {
    $ok = $results[$key]
    if (-not $ok) { $allOk = $false }
    Write-Host ("{0,-50} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
}
Write-Host ""
if ($allOk) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL" }
