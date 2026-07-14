# ADR-035 -- stepping through a query typed at the prompt, with a CHOICE POINT in it:
#
#   shumway --debug                 (no file at all)
#   attach from Visual Studio
#   ?- writeln(uno), member(X, [a,b,c]), debugger_break, writeln(dos(X)).
#   ... then F10, F10, F10, ...
#
# The user's report: after a couple of F10 it dies with "Unable to step. Operation not
# supported. Unknown error: 0x80004005", and the call stack has NO Prolog frames left --
# only the REPL's C#. So this drives exactly that, and prints what the engine and the
# component think at every step, instead of guessing.
#
# Run from Windows PowerShell 5.1. ASCII only.

$ErrorActionPreference = 'Stop'

$devenv = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl   = (Resolve-Path (Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe")).Path

$Steps = 4   # F10s to take after the debugger_break stop

# A DEADLINE, because the thing being tested is a debugger that can hang. Every wait below
# checks it, so a wedged IDE ends the run instead of owning the machine: the finally block
# kills devenv and the engine either way.
$DeadlineSeconds = 420
$deadline = (Get-Date).AddSeconds($DeadlineSeconds)
function Assert-Time([string]$what) {
    if ((Get-Date) -gt $deadline) { throw "DEADLINE ($DeadlineSeconds s) reached while: $what" }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RotFinder6
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

try {
    Write-Host "[1/4] shumway --debug, no file ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $repl
    $psi.Arguments = "--debug"
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $engine = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 3

    Write-Host "[2/4] devenv, and attach ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    $dte = Invoke-WithRetry { $d = [RotFinder6]::FindDte($vsProc.Id); if (-not $d) { throw "no DTE" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10
    $target = Invoke-WithRetry {
        $p = @($dte.Debugger.LocalProcesses) | Where-Object { $_.ProcessID -eq $engine.Id }
        if (-not $p) { throw "not in LocalProcesses" }
        $p
    } 30 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Start-Sleep -Seconds 6

    function Wait-ForBreak([int]$Seconds = 30) {
        for ($i = 0; $i -lt $Seconds; $i++) {
            Assert-Time "waiting for a break"
            $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
            if ($m -eq 2) { return $true }
            Start-Sleep -Seconds 1
        }
        return $false
    }

    # Every thread's frames, so a Prolog stack spliced onto the WRONG thread shows up as
    # what it is. Returns the frames of the thread that has Prolog on it, or an empty list.
    function Prolog-Frames {
        Invoke-WithRetry {
            $out = @()
            foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
                $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
                if (@($names | Where-Object { $_ -match '^(\w+/-?\d+|\?- )' -or $_ -match '!\d+$' }).Count -gt 0) {
                    $dte.Debugger.CurrentThread = $t
                    $out = $names
                    break
                }
            }
            $out
        } 5 2000
    }
    function Diag-Line {
        Invoke-WithRetry {
            foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
                foreach ($n in @($t.StackFrames) | ForEach-Object { $_.FunctionName }) {
                    if ($n -match '^\[Shumway diag\]') { return $n }
                }
            }
            return ""
        } 3 1000
    }

    Write-Host "[3/4] typing the query -- with a CHOICE POINT in it ..."
    $engine.StandardInput.WriteLine("writeln(uno), member(X, [a,b,c]), debugger_break, writeln(dos(X)).")
    $engine.StandardInput.Flush()

    $broke = Wait-ForBreak 40
    Write-Host "  stopped at debugger_break: $broke"
    if ($broke) {
        Write-Host "=== stack ==="
        Prolog-Frames | ForEach-Object { Write-Host "   $_" }
        Write-Host (Diag-Line)
        Write-Host "============="
    }
    $results["Q1 debugger_break stops, with a Prolog stack"] =
        ($broke -and @(Prolog-Frames).Count -gt 0)

    # Each F10 must land at a real port, WHILE PORTS EXIST. Stepping past the last goal of
    # the query is not a failure: the query hands back its answer and stands still, no port
    # can satisfy the step, and the right thing is for the debugger to drop it and let the
    # program run on (StopReason.StepAbandoned). What must NOT happen is what the user saw --
    # Visual Studio waiting forever for a stop that is not coming, and answering the next key
    # with "Unable to step. Operation not supported."
    Write-Host "[4/4] $Steps x F10 ..."
    $ported = 0
    $ranOn = $false
    $threw = ""
    for ($s = 1; $s -le $Steps; $s++) {
        $mode = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
        if ($mode -ne 2) {
            # The step was abandoned and the program is running again -- which is the answer,
            # not a failure. There is nothing left to step.
            Write-Host "  F10 #${s}: the program is running again (the step was dropped)"
            $ranOn = $true
            break
        }
        try {
            # NOT StepOver($true). That means "block until the debugger breaks again" -- and
            # a step that gets ABANDONED (the program ran on, which is the case under test)
            # never breaks again, so the COM call never returns and the IDE looks wedged. Ask
            # for the step, then watch for the outcome ourselves, with a clock.
            Invoke-WithRetry { $dte.Debugger.StepOver($false) } 3 2000
        } catch {
            $threw = $_.Exception.Message
            Write-Host "  F10 #${s}: THREW -- $threw"
            break
        }
        $mode = 1
        for ($w = 0; $w -lt 8; $w++) {
            Start-Sleep -Seconds 1
            $mode = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
            if ($mode -eq 2) { break }
        }
        $frames = if ($mode -eq 2) { @(Prolog-Frames) } else { @() }
        Write-Host "  F10 #${s}: mode=$mode  top=$(if ($frames.Count) { $frames[0] } else { '<running>' })"
        if ($mode -eq 2 -and $frames.Count -gt 0) { $ported++ }
    }
    # ONE real port is the honest count for this query: everything in it but the query itself
    # is a builtin or the prelude's (member/2), and those are `:- disable_debug` -- a step no
    # longer wanders into copy_term/3 or $prelude$$attr_goals_of/2, which is where it used to
    # leave the user. So: the exit port of the query, and then there is nothing left to step.
    $results["Q2 F10 lands at a real port"] = ($ported -ge 1)
    $results["Q3 stepping past the query drops the step (no 0x80004005)"] =
        ($ranOn -and $threw -eq "")

    Write-Host ""
    Write-Host "--- results ---"
    $allOk = $true
    foreach ($key in $results.Keys) {
        $ok = $results[$key]
        if (-not $ok) { $allOk = $false }
        Write-Host ("{0,-46} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
    }
    Write-Host ""
    if ($allOk) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL" }
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($engine -and -not $engine.HasExited) { $engine.Kill() } } catch {}
    Get-Process shumway -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
