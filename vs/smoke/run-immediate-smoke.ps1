# ADR-035 -- THE IMMEDIATE WINDOW RUNS GOALS.
#
# Stopped at a breakpoint, the user types a goal into the Immediate window. It runs in a
# fresh activation over the live engine, with the frame's variables substituted by their
# current values -- database side effects and all.
#
#   I1  a goal with the frame's variable substituted answers with its bindings
#   I2  an assertz lands in the LIVE database, and a second goal reads it back
#   I3  a goal that reaches a breakpoint STOPS there -- nested break, mixed stack -- and
#       F5 lets the evaluation finish
#
# Run from Windows PowerShell 5.1. ASCII only.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl    = (Resolve-Path (Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe")).Path
$program = (Resolve-Path (Join-Path $PSScriptRoot "immediate.pl")).Path

$DeadlineSeconds = 420
$deadline = (Get-Date).AddSeconds($DeadlineSeconds)
function Assert-Time([string]$what) {
    if ((Get-Date) -gt $deadline) { throw "DEADLINE ($DeadlineSeconds s) reached while: $what" }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RotFinder9
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
        Assert-Time "retrying an IDE call"
        try { return & $Action } catch {
            if ($i -eq $Attempts) { throw }
            Start-Sleep -Milliseconds $DelayMs
        }
    }
}

# Evaluate a goal from the Immediate window ON ANOTHER THREAD. A goal that reaches a
# breakpoint stops inside itself -- a nested break -- and the call that started it does not
# return until that break is released, exactly as a C# Immediate-window call does not return
# while you are stopped inside it. So this script cannot be the one to make the call, or it
# would block on its own nested stop with no hand free to press F5. A second process makes
# it (re-finding the same devenv by pid through the running-object table) and blocks; this
# one stays free to look at the nested stack and continue.
function Start-EvalJob([int]$VsPid, [string]$Goal) {
    Start-Job -ArgumentList $VsPid, $Goal -ScriptBlock {
        param($vsPid, $goal)
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RotFinder9J
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
        for ($i = 0; $i -lt 30; $i++) {
            try {
                $d = [RotFinder9J]::FindDte($vsPid)
                if ($d) { return $d.Debugger.GetExpression($goal, $true, 90000).Value }
            } catch { }
            Start-Sleep -Milliseconds 1000
        }
        return "<the job never reached the IDE>"
    }
}

# A nested stop cannot be observed from this thread while the func-eval is in flight -- the
# IDE's automation channel is closed ("call rejected") for the whole time the evaluation
# holds. So a nested break is asserted against the COMPONENT LOG, the debugger's own record
# of the stop it was handed: wait for a snapshot naming <goal> with at least <minFrames>
# frames (the mixed stack -- the evaluated goal's frames, the boundary, and the outer ones
# under it), then release it with F5 and collect the job's answer.
function Wait-NestedStopThenContinue([string]$GoalName, [int]$MinFrames, $Job) {
    $seen = $false
    for ($i = 0; $i -lt 25; $i++) {
        Assert-Time "waiting for the nested break at $GoalName"
        Start-Sleep -Seconds 1
        $log = if (Test-Path $componentLog) { Get-Content $componentLog } else { @() }
        foreach ($line in $log) {
            if ($line -match "snapshot: Breakpoint .*frames=(\d+) goal=$GoalName" -and [int]$Matches[1] -ge $MinFrames) {
                $seen = $true; break
            }
        }
        if ($seen) { break }
    }
    # F5: release the nested stop; the goal runs on to its answer. Go can transiently refuse
    # ("Unable to execute method at this time") while the func-eval is mid-transition, so
    # retry it a few times, but never let it kill the run -- the answer coming back from the
    # job is the real proof the stop released.
    for ($g = 0; $g -lt 10; $g++) {
        try { $mode = $dte.Debugger.CurrentMode } catch { $mode = -1 }
        Write-Host ("  releasing nested stop (mode={0}) ..." -f $mode)
        try { $dte.Debugger.Go($false); break } catch { Start-Sleep -Seconds 2 }
    }
    $answer = "<no answer>"
    if (Wait-Job $Job -Timeout 90) { $answer = (Receive-Job $Job | Select-Object -Last 1) }
    Remove-Job $Job -Force -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Stopped = $seen; Answer = $answer }
}

$vsProc = $null
$engine = $null
$results = [ordered]@{}
$componentLog = Join-Path $env:TEMP "shumway-debug\component.log"
Remove-Item $componentLog -ErrorAction SilentlyContinue

try {
    Write-Host "[1/6] shumway --debug immediate.pl ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $repl
    $psi.Arguments = "--debug `"$program`""
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $engine = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 2

    Write-Host "[2/6] devenv, attach ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    $dte = Invoke-WithRetry { $d = [RotFinder9]::FindDte($vsProc.Id); if (-not $d) { throw "no DTE" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10
    $target = Invoke-WithRetry {
        if ($engine.HasExited) { throw "the engine exited" }
        $p = @($dte.Debugger.LocalProcesses) | Where-Object { $_.ProcessID -eq $engine.Id }
        if (-not $p) { throw "engine pid $($engine.Id) not in LocalProcesses yet" }
        $p
    } 45 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Start-Sleep -Seconds 8

    Write-Host "[3/6] breakpoint inside step/1, and run go ..."
    # Line 11 is `    helper(N).` -- step/1's body.
    Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $program, 11) | Out-Null } 10 2000
    $engine.StandardInput.WriteLine("go.")
    $engine.StandardInput.Flush()

    $stopped = $false
    for ($i = 0; $i -lt 25; $i++) {
        Assert-Time "waiting for the breakpoint"
        Start-Sleep -Seconds 1
        if ((Invoke-WithRetry { $dte.Debugger.CurrentMode } 5 1000) -eq 2) { $stopped = $true; break }
    }
    if (-not $stopped) { throw "the breakpoint never hit" }

    # Select the Prolog frame so the Immediate window evaluates in OUR language.
    Invoke-WithRetry {
        $all = @()
        foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
            $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
            $all += ("    thread {0}: {1}" -f $t.ID, (($names | Select-Object -First 4) -join " | "))
            if (@($names | Where-Object { $_ -match '!\d+$' -or $_ -match '^\w+/\d+$' -or $_ -match '^\?- ' }).Count -gt 0) {
                $dte.Debugger.CurrentThread = $t
                return
            }
        }
        $all | ForEach-Object { Write-Host $_ }
        throw "no Prolog thread yet"
    } 10 2000
    Start-Sleep -Seconds 2
    Write-Host ("  stopped, current frame: {0}" -f $dte.Debugger.CurrentStackFrame.FunctionName)

    Write-Host "[4/6] I0: does GetExpression even reach our evaluator? (a bare variable) ..."
    $r0 = Invoke-WithRetry { $dte.Debugger.GetExpression("N", $true, 15000) } 3 2000
    Write-Host ("  N -> IsValid={0}  Value={1}" -f $r0.IsValidValue, $r0.Value)

    Write-Host "[4/6] I1: double(N, R) with N substituted from the frame ..."
    $r1 = Invoke-WithRetry { $dte.Debugger.GetExpression("double(N, R)", $true, 30000) } 3 2000
    Write-Host ("  IsValid={0}  Value={1}" -f $r1.IsValidValue, $r1.Value)
    $results["I1 a goal runs with the frame's N substituted"] =
        ($r1.IsValidValue -and $r1.Value -match "R = 42")

    Write-Host "[5/6] I2: assertz into the live database, read it back ..."
    $r2a = Invoke-WithRetry { $dte.Debugger.GetExpression("assertz(seen(N))", $true, 30000) } 3 2000
    $r2b = Invoke-WithRetry { $dte.Debugger.GetExpression("seen(Q)", $true, 30000) } 3 2000
    Write-Host ("  assertz -> {0}   seen(Q) -> {1}" -f $r2a.Value, $r2b.Value)
    $results["I2 assertz persists and reads back"] =
        ($r2a.Value -match "true" -and $r2b.Value -match "Q = 21")

    # I3: THE USER'S BUG, and the nested-break case in one. Stopped at step/1, draw a NEW
    # breakpoint on double/2 (line 8) -- which goes down the command channel and sits there
    # unread, the engine being parked in this stop -- then evaluate a goal that reaches it.
    # Before the fix the evaluation ran straight past the breakpoint (nothing had applied it
    # yet, and only a breakpoint set BEFORE the stop worked); now the evaluation drains and
    # applies the pending breakpoint FIRST, and stops there -- a nested break, on top of the
    # one we were already in. (It subsumes the pre-set-breakpoint nested break the previous
    # run checked: same mechanism, harder case. This is the LAST nested-eval leg because
    # releasing it with Go lets the outer `go.` query run on to completion.)
    Write-Host "[6/6] I3: a breakpoint set WHILE STOPPED stops the evaluation -- nested break ..."
    Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $program, 8) | Out-Null } 10 2000
    Start-Sleep -Seconds 1
    $bpEvalJob = Start-EvalJob $vsProc.Id "double(21, R)"
    # A stop whose goal is double/2 at all is the nested one -- every outer stop is step/1.
    # 5 keeps it clear of the 4-frame outer stack without over-fitting the query wrapper.
    $r2c = Wait-NestedStopThenContinue "double/2" 5 $bpEvalJob
    Write-Host ("  stopped at the just-set breakpoint: {0}   answer: {1}" -f $r2c.Stopped, $r2c.Answer)
    $results["I3 a breakpoint set while stopped stops the evaluation (nested break)"] =
        $r2c.Stopped
    # I4: releasing the nested stop RELEASED it -- the goal ran on to its answer and handed
    # it back (Wait-NestedStopThenContinue pressed F5 and collected it).
    $results["I4 the released evaluation answers"] = ($r2c.Answer -match "R = 42")

    if (Test-Path $componentLog) {
        Write-Host ""
        Write-Host "--- component log (tail) ---"
        Get-Content $componentLog | Where-Object { $_ -notmatch "module load:" } |
            Select-Object -Last 10 | ForEach-Object { Write-Host "   $_" }
    }

    Write-Host ""
    Write-Host "--- results ---"
    $allOk = $true
    foreach ($key in $results.Keys) {
        $ok = $results[$key]
        if (-not $ok) { $allOk = $false }
        Write-Host ("{0,-55} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
    }
    Write-Host ""
    if ($allOk) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL" }
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($engine -and -not $engine.HasExited) { $engine.Kill() } } catch {}
}
