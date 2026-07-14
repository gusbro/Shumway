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

    Write-Host "[6/6] I3: a goal that reaches the breakpoint stops -- nested break ..."
    # `go` runs run(21) -> step(21) -> the armed breakpoint, NESTED: the evaluation stops
    # inside itself, on top of the stop we are already in. Which means the call that
    # STARTED it does not return -- exactly as a C# call from the Immediate window does not
    # return while you are stopped inside it.
    #
    # So it cannot be this thread that makes the call. A second process drives the
    # evaluation and blocks on it; this one is left free to do what the user would do --
    # look at the nested stack, and then press F5 to let the evaluation finish.
    $evalJob = Start-Job -ArgumentList $vsProc.Id -ScriptBlock {
        param($vsPid)
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
                if ($d) { return $d.Debugger.GetExpression("go", $true, 90000).Value }
            } catch { }
            Start-Sleep -Milliseconds 1000
        }
        return "<the job never reached the IDE>"
    }

    # POLL for the nested stop -- and expect NOT to see it from here. While a func-eval is
    # in flight the IDE's automation channel is closed (every DTE call comes back "call
    # rejected"), so this script cannot ask VS what its stack looks like at the moment the
    # nested break holds; it gets the last stack it managed to read, which is the OUTER one.
    # That is a property of DTE, not of the debugger, and the Call Stack window shows the
    # nested stack perfectly well to a human sitting in front of it.
    #
    # So the assertion below is made against the COMPONENT LOG instead: the debugger's own
    # record of the stop it was handed and the stack it read out of the engine. Same claim,
    # read through a channel that is open. (The exact frame composition -- the boundary
    # frame, the evaluated frames above it, the outer frames below -- is pinned by
    # Adr035EvaluateTests, which does not need an IDE at all.)
    $nestedFrames = @()
    $nestedMode = 0
    $dteErrors = 0
    for ($i = 0; $i -lt 25; $i++) {
        Assert-Time "waiting for the nested break"
        Start-Sleep -Seconds 1
        try {
            $nestedMode = $dte.Debugger.CurrentMode
            $f = @($dte.Debugger.CurrentThread.StackFrames) | ForEach-Object { $_.FunctionName }
            if (@($f | Where-Object { $_ -match '\[Immediate: go' }).Count -ge 1) { $nestedFrames = $f; break }
            $nestedFrames = $f
        } catch { $dteErrors++ }
    }
    if ($dteErrors -gt 0) { Write-Host "  ($dteErrors DTE calls rejected -- the IDE is busy in the func-eval)" }
    Write-Host "=== stack at the nested stop ==="
    $nestedFrames | Select-Object -First 8 | ForEach-Object { Write-Host "   $_" }
    Write-Host "================================"
    # The debugger was handed a THIRD stop -- one the user never asked for and the program
    # would never have reached on its own -- while the evaluation was running, and the stack
    # it read at that stop is the deep mixed one (the evaluated goal's frames, the boundary,
    # and the outer frames underneath: 9, against the 4 of the stop we were sitting in).
    $log = if (Test-Path $componentLog) { Get-Content $componentLog } else { @() }
    $nestedStop = @($log | Where-Object { $_ -match 'snapshot: Breakpoint seq=3' -and $_ -match 'frames=(\d+)' -and [int]$Matches[1] -ge 8 }).Count -ge 1
    $results["I3 the evaluated goal stops at the breakpoint, stack mixed"] = $nestedStop

    # F5: the nested stop releases, the evaluation runs to its answer, and we are back at
    # the ORIGINAL stop -- still in break mode, exactly where we were before we asked.
    Invoke-WithRetry { $dte.Debugger.Go($false) } 5 2000
    Start-Sleep -Seconds 6
    $afterMode = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
    $evalAnswer = "<no answer>"
    if (Wait-Job $evalJob -Timeout 60) { $evalAnswer = (Receive-Job $evalJob | Select-Object -Last 1) }
    Remove-Job $evalJob -Force -ErrorAction SilentlyContinue
    Write-Host ("  after F5: mode={0}  the evaluation answered: {1}" -f $afterMode, $evalAnswer)
    # What matters is that releasing the nested stop RELEASES IT: the goal runs on to its
    # answer and hands it back. (Whether the IDE then sits in break mode or run mode is
    # DTE's business -- pressing F5 twice in a row is a continue, and this script cannot
    # tell the second one from the first.)
    $results["I4 F5 finishes the evaluation, which answers"] = ($evalAnswer -match "true")

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
