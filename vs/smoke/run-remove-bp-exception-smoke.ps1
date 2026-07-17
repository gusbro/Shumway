# ADR-035 diagnostic smoke -- the user's report: stopped at a breakpoint, REMOVE the
# breakpoint, wait a few moments -> VS Output shows first-chance PrologRuntimeExceptions
# (in a process whose threads are all supposedly frozen). Who is running Prolog?
#
#   R-1  reproduce: stop at a bp, delete it, wait 15 s still stopped, read the Output
#        window's Debug pane -- did "Exception thrown: 'Shumway.Core.PrologRuntimeException'"
#        lines appear AFTER the delete?
#   R-2  if yes: turn ON break-on-thrown for that exception, repeat the trigger, and when
#        the debugger breaks dump the throwing thread's C# stack -- the exact thrower.
#
# Run from Windows PowerShell 5.1. ASCII only.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$work    = Join-Path $PSScriptRoot "condbp-ws-work"
$compile = Join-Path $PSScriptRoot "..\..\src\Shumway.Compile\bin\x64\Release\net10.0\shumway-compile.exe"
$link    = Join-Path $PSScriptRoot "..\..\src\Shumway.Link\bin\x64\Release\net10.0\shumway-link.exe"

foreach ($f in @($devenv, $compile, $link)) { if (-not (Test-Path $f)) { throw "missing $f" } }

New-Item -ItemType Directory -Force -Path $work | Out-Null
$pl = Join-Path $work "wsapp.pl"
@'
:- public main/0.
main :-
    repeat,
    between(1, 100000, N),
    tick(N, _),
    fail.

tick(N, Doubled) :-
    mid(N),
    Doubled is N * 2.

mid(_).
'@ | Out-File -FilePath $pl -Encoding ascii

$lines = Get-Content $pl
$lineA = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'mid\(N\)') { $lineA = $i + 1 }
}
Write-Host "breakpoint line: $lineA -> $($lines[$lineA - 1].Trim())"

Write-Host "[0/5] compile --debug + link --exe --debug ..."
& $compile --debug $pl -o (Join-Path $work "wsapp.shmo")
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
& $link (Join-Path $work "wsapp.shmo") --goal main --exe (Join-Path $work "wsapp") --debug
if ($LASTEXITCODE -ne 0) { throw "link failed" }
$exe = Join-Path $work "wsapp.exe"

$stderrLog = Join-Path $env:TEMP "shumway-rmbp-stderr.log"
$stdoutLog = Join-Path $env:TEMP "shumway-rmbp-stdout.log"
Remove-Item $stderrLog, $stdoutLog -ErrorAction SilentlyContinue

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class RotFinderRm
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
    Write-Host "[1/5] starting wsapp.exe ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $dbgProc = Start-Process -FilePath $exe -PassThru -NoNewWindow `
        -RedirectStandardError $stderrLog -RedirectStandardOutput $stdoutLog
    Write-Host "  pid $($dbgProc.Id)"

    Write-Host "[2/5] starting devenv /rootsuffix Exp + attach ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    Remove-Item Env:\SHUMWAY_DEBUG_DIAG
    $dte = Invoke-WithRetry { $d = [RotFinderRm]::FindDte($vsProc.Id); if ($null -eq $d) { throw "no DTE yet" }; $d } 90 2000
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
            $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
            if ($m -eq 2) { return $true }
            Start-Sleep -Seconds 1
        }
        return $false
    }

    function Get-DebugOutputText {
        try {
            $pane = $dte.ToolWindows.OutputWindow.OutputWindowPanes.Item("Debug")
            $sel = $pane.TextDocument.Selection
            $sel.StartOfDocument($false)
            $sel.EndOfDocument($true)
            return $sel.Text
        } catch { return "" }
    }

    Write-Host "[3/5] Break All twice (module bootstrap) ..."
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Start-Sleep -Seconds 4
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null

    $bpFile = $pl
    try {
        $doc = $dte.ActiveDocument
        if ($doc -and $doc.FullName -match 'wsapp\.pl$') { $bpFile = $doc.FullName }
    } catch { }

    Write-Host "[4/5] bp at line $lineA; F5; stop; DELETE the bp; wait 15 s still stopped ..."
    Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $bpFile, $lineA) | Out-Null } 10 2000
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    $stopped = Wait-ForBreak 30
    Write-Host "  stopped at the bp: $stopped"
    $results["R-0 stopped at the breakpoint"] = $stopped
    Select-PrologThread | Out-Null

    $exRegex = "Exception thrown: 'Shumway\.Core\.PrologRuntimeException'"

    # --- R-3: the WATCH refresh. Visual Studio re-evaluates every Watch/QuickWatch
    # expression whenever its debugger UI reactivates (the user's focus-switch repro).
    # GetExpression takes exactly that path: our EE -> func-eval -> the goal runs in the
    # engine. An expression that ERRORS in Prolog (a bare variable goal, an unknown
    # predicate) throws inside the engine -- first-chance lines in the Output, one batch
    # PER REFRESH. With the new SHUMWAY_DEBUG_DIAG first-chance logger, the debuggee's
    # stderr now carries the FULL C# stack of each, naming the thrower.
    $watchBase = ([regex]::Matches((Get-DebugOutputText), $exRegex)).Count
    Write-Host "  [R-3] evaluating watch-style expression 'LineNo' twice (focus-switch repro) ..."
    $watchText = ""
    try {
        $expr = $dte.Debugger.GetExpression("LineNo", $true, 10000)
        $watchText = "$($expr.Value)"
        $null = $dte.Debugger.GetExpression("LineNo", $true, 10000)
    } catch { Write-Host "  GetExpression threw: $($_.Exception.Message)" }
    Start-Sleep -Seconds 3
    $watchAfter = ([regex]::Matches((Get-DebugOutputText), $exRegex)).Count
    $watchNew = $watchAfter - $watchBase
    Write-Host "  watch value: '$watchText'; new exception lines from 2 evaluations: $watchNew"
    # INFORMATIONAL, not asserted: what this leg shows depends on the host VS's func-eval
    # mode. With ForceRealFuncEval (the user's VS) the goal really runs and an erroring
    # expression logs first-chance exceptions per refresh — the focus-switch spam. In a
    # bare hive the IMPLICIT evaluation runs under the func-eval INTERPRETER, which dies at
    # the first internal call (System.Array.Clear in DrainCommands) before the goal runs.
    $results["R-3 watch-eval leg ran (informational)"] = $true

    # Baseline BEFORE the delete: how many exception lines are already there.
    $before = ([regex]::Matches((Get-DebugOutputText), $exRegex)).Count
    Write-Host "  exception lines before delete: $before"

    foreach ($bp in @($dte.Debugger.Breakpoints)) { $bp.Delete() }
    Write-Host "  breakpoint deleted; waiting 15 s (still in break mode) ..."
    Start-Sleep -Seconds 15

    $mode = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
    $after = ([regex]::Matches((Get-DebugOutputText), $exRegex)).Count
    $newEx = $after - $before
    Write-Host "  exception lines after: $after (new: $newEx); debugger mode: $mode (2 = still break)"
    $results["R-1 no new first-chance exceptions after delete"] = ($newEx -eq 0)

    # --- R-2: if it reproduced, catch the thrower red-handed ---
    if ($newEx -gt 0) {
        Write-Host "[5/5] REPRODUCED. Break-on-thrown + repeat to catch the thrower ..."
        try {
            $group = $dte.Debugger.ExceptionGroups.Item("Common Language Runtime Exceptions")
            try { $group.SetBreakWhenThrown($true, "Shumway.Core.PrologRuntimeException") }
            catch {
                $group.NewException("Shumway.Core.PrologRuntimeException", 0)
                $group.SetBreakWhenThrown($true, "Shumway.Core.PrologRuntimeException")
            }
            # Re-arm the trigger: add the bp again, continue, stop, delete, wait for the
            # exception break.
            Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $bpFile, $lineA) | Out-Null } 10 2000
            Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
            if (Wait-ForBreak 30) {
                foreach ($bp in @($dte.Debugger.Breakpoints)) { $bp.Delete() }
                # If the throw happens while stopped, VS flips to a NEW break state on the
                # exception; watch for the mode to leave-and-return or the stack to change.
                Start-Sleep -Seconds 15
                Write-Host "  === current thread stack at/after the window ==="
                foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
                    $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
                    if (@($names | Where-Object { $_ -match 'Shumway' }).Count -gt 0) {
                        Write-Host "  --- thread $($t.ID) ---"
                        $names | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" }
                    }
                }
            }
        } catch { Write-Host "  break-on-thrown setup threw: $($_.Exception.Message)" }
    } else {
        Write-Host "[5/5] not reproduced -- nothing to catch."
    }
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($dbgProc -and -not $dbgProc.HasExited) { $dbgProc.Kill() } } catch {}
}

Start-Sleep -Seconds 2
Write-Host ""
Write-Host "=== engine stderr (tail: first-chance stacks + condition lines) ==="
if (Test-Path $stderrLog) { Get-Content $stderrLog | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" } }

Write-Host ""
Write-Host "--- results ---"
$allOk = $true
foreach ($key in $results.Keys) {
    $ok = $results[$key]
    if (-not $ok) { $allOk = $false }
    Write-Host ("{0,-50} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
}
Write-Host ""
if ($allOk) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL (reproduced -- see the thread stacks above)" }
