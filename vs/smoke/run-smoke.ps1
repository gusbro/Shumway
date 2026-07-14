# ADR-035 D2 + D3 smoke check -- the REAL engine, driven through Visual Studio.
#
# Launches shumway.exe --debug on smoke.pl, starts devenv /rootsuffix Exp, attaches the
# managed debugger via DTE, and then asks the six questions D2 and D3 exist to answer:
#
#   D2-1  the call stack shows PROLOG frames, and none of the engine's own
#   D2-2  a Prolog frame's Locals show that clause's variables, as terms
#   D3-1  F9 in a .pl binds, and execution stops there
#   D3-2  the stop opens the .pl at the right line (source navigation)
#   D3-3  F11 steps, and lands somewhere else
#   D3-4  F5 continues, and the program is running again
#
# Everything before this script is a hypothesis: the components compile and the engine's
# own tests pass, but nothing had ever run inside the IDE.
#
# Run from Windows PowerShell 5.1 (COM ROT access). Keep this file ASCII-only -- PS 5.1
# reads .ps1 as CP1252, where a UTF-8 em-dash decodes to a quote and silently terminates
# a string literal mid-line.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl    = Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe"
$program = Join-Path $PSScriptRoot "smoke.pl"

if (-not (Test-Path $repl))    { throw "build the REPL first (dotnet build src\Shumway.Repl): $repl not found" }
if (-not (Test-Path $program)) { throw "missing $program" }

$repl    = (Resolve-Path $repl).Path
$program = (Resolve-Path $program).Path

# The breakpoint line: the body of tick/2, which is the last non-empty line of the file.
$lines = Get-Content $program
$bpLine = 0
for ($i = $lines.Count - 1; $i -ge 0; $i--) {
    if ($lines[$i].Trim().Length -gt 0) { $bpLine = $i + 1; break }
}
Write-Host "breakpoint line in smoke.pl: $bpLine  ->  $($lines[$bpLine - 1].Trim())"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class RotFinder
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    public static object FindDte(int pid)
    {
        IRunningObjectTable rot;
        GetRunningObjectTable(0, out rot);
        IEnumMoniker enumMoniker;
        rot.EnumRunning(out enumMoniker);
        IMoniker[] moniker = new IMoniker[1];
        IntPtr fetched = IntPtr.Zero;
        string suffix = ":" + pid;
        while (enumMoniker.Next(1, moniker, fetched) == 0)
        {
            IBindCtx bindCtx;
            CreateBindCtx(0, out bindCtx);
            string displayName;
            moniker[0].GetDisplayName(bindCtx, null, out displayName);
            if (displayName.StartsWith("!VisualStudio.DTE.") && displayName.EndsWith(suffix))
            {
                object dte;
                rot.GetObject(moniker[0], out dte);
                return dte;
            }
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
    Write-Host "[1/6] starting shumway.exe --debug ..."
    $env:SHUMWAY_GOAL = "main."
    $dbgProc = Start-Process -FilePath $repl -ArgumentList "--debug", "`"$program`"" `
        -PassThru -WindowStyle Minimized
    Remove-Item Env:\SHUMWAY_GOAL
    Write-Host "  pid $($dbgProc.Id)"

    Write-Host "[2/6] starting devenv /rootsuffix Exp (first launch can be slow)..."
    # Makes the components report what the IDE and the MONITOR side each managed to do, as
    # an extra frame at the bottom of the Prolog stack. The monitor side has no other voice.
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    Remove-Item Env:\SHUMWAY_DEBUG_DIAG
    $dte = Invoke-WithRetry { $d = [RotFinder]::FindDte($vsProc.Id); if ($null -eq $d) { throw "no DTE yet" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    Write-Host "[3/6] attaching to shumway.exe ..."
    $target = Invoke-WithRetry {
        $p = @($dte.Debugger.LocalProcesses) | Where-Object { $_.ProcessID -eq $dbgProc.Id }
        if (-not $p) { throw "debuggee not in LocalProcesses yet" }
        $p
    } 30 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Start-Sleep -Seconds 5

    # The engine is not the only thread in the process: the REPL runs an ESC watcher, and
    # a Break All is as likely to land the IDE's current thread on that as on the one
    # doing the work. Find the thread whose stack has Prolog on it -- which, if the stack
    # filter is doing its job, is the only place Prolog appears at all.
    function Select-PrologThread {
        Invoke-WithRetry {
            $chosen = $null
            foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
                $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
                # Prolog frames if the filter is doing its job; the raw interpreter if it
                # is not. Either way it is the thread running the query -- and NOT the
                # REPL's ESC watcher, which is also a Shumway frame and is not it.
                # -match, not -like: in a -like pattern "[Shumway]" is a CHARACTER CLASS, so
                # it matches any frame containing an 's' or an 'a' -- which is every frame in
                # the process, and it quietly picked the wrong thread for two runs.
                $hit = @($names | Where-Object {
                    $_ -match '^\w+/\d+$' -or $_ -match 'BytecodeInterpreter|PrologEngine' `
                        -or $_ -match '^\[Shumway'
                })
                if ($hit.Count -gt 0) { $chosen = $t; break }
            }
            if (-not $chosen) { throw "no thread with Prolog on it yet" }
            $dte.Debugger.CurrentThread = $chosen
            $now = $dte.Debugger.CurrentThread
            Write-Host ("  current thread: {0} (wanted {1})" -f $now.ID, $chosen.ID)
            if ($now.ID -ne $chosen.ID) { throw "CurrentThread did not take" }
            $chosen
        } 10 2000
        # Selecting the thread is not cosmetic: Visual Studio only runs code in the
        # debuggee on the CURRENT thread, and an asynchronous break has to ASK the engine
        # where it is (the answer does not exist until someone walks the environment
        # chain). With the thread selected, its stack is walked again and the question can
        # be put. Give that second walk a moment.
        Start-Sleep -Seconds 2
    }

    function Get-Frames {
        Invoke-WithRetry {
            $f = @($dte.Debugger.CurrentThread.StackFrames) | ForEach-Object { $_.FunctionName }
            if (-not $f) { throw "no frames yet" }
            $f
        } 10 2000
    }

    # EnvDTE.dbgDebugMode: 1 = design, 2 = break, 3 = run.
    function Wait-ForBreak([int]$Seconds = 30) {
        for ($i = 0; $i -lt $Seconds; $i++) {
            $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
            if ($m -eq 2) { return $true }
            Start-Sleep -Seconds 1
        }
        return $false
    }
    function Show-Frames([string]$title, $frames) {
        Write-Host ""
        Write-Host "=== $title ==="
        $frames | ForEach-Object { Write-Host "  $_" }
        Write-Host "============================"
    }

    # Two breaks. The first teaches the IDE side which .pl files exist (it learns them
    # from the snapshot, in the stack filter); the modules that make frames navigable are
    # created by the SERVER, which can only do it in a real event context -- so they
    # appear at the NEXT pause. That is by design, and this is what it looks like.
    Write-Host "[4/6] Break All (first) ..."
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null
    Show-Frames "call stack, break #1" (Get-Frames)

    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Start-Sleep -Seconds 4

    Write-Host "Break All (second) ..."
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null
    $frames = Get-Frames
    Show-Frames "call stack, break #2" $frames

    # --- D2-1: Prolog frames, and none of the engine's own ---
    $prolog   = @($frames | Where-Object { $_ -match "^(main|tick|loop)/\d" })
    $engine   = @($frames | Where-Object { $_ -like "*BytecodeInterpreter*" -or $_ -like "*Activation*" })
    $results["D2-1 prolog frames replace the engine"] = ($prolog.Count -ge 1 -and $engine.Count -eq 0)

    # --- D2-1b: and the Call Stack window calls them PROLOG ---
    # The Language column reads the frame's DkmCompilerId back out of the registry
    # (AD7Metrics\ExpressionEvaluator\<language>\<vendor>). With nothing registered there the
    # frames were right and the column said "unknown", which is what the user saw.
    $langs = Invoke-WithRetry {
        @($dte.Debugger.CurrentThread.StackFrames) |
            Where-Object { $_.FunctionName -match "^(main|tick|loop)/\d" } |
            ForEach-Object { $_.Language }
    } 10 2000
    Write-Host ("  languages: {0}" -f ($langs -join ", "))
    $results["D2-1b the language column says Prolog"] =
        ($langs.Count -ge 1 -and @($langs | Where-Object { $_ -ne "Prolog" }).Count -eq 0)

    # --- D3-1 / D3-2: F9 in the .pl, stop there, open it there ---
    Write-Host "[5/6] setting a breakpoint at smoke.pl:$bpLine ..."
    $bpAdded = $true
    try { Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $program, $bpLine) | Out-Null } 10 2000 }
    catch { $bpAdded = $false; Write-Host "  Breakpoints.Add threw: $($_.Exception.Message)" }

    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    $stopped = Wait-ForBreak 30
    $mode = if ($stopped) { 2 } else { Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 2000 }
    $results["D3-1 F9 binds and execution stops there"] = ($bpAdded -and $stopped)
    Write-Host "  stopped at the breakpoint: $stopped (mode $mode; 2 = break, 3 = run)"

    $docName = "(none)"; $docLine = 0
    $localsText = "(none)"
    if ($mode -eq 2) {
        Select-PrologThread | Out-Null
        Show-Frames "call stack, at the breakpoint" (Get-Frames)
        try {
            $doc = $dte.ActiveDocument
            $docName = $doc.FullName
            $docLine = $doc.Selection.CurrentLine
        } catch { $docName = "threw: $($_.Exception.Message)" }

        # --- D2-2: the LOCALS of the clause we are stopped in. tick/2 has N (bound) and
        # Doubled (not yet) -- the engine renders both; nothing here can.
        try {
            $plFrame = @($dte.Debugger.CurrentThread.StackFrames) |
                Where-Object { $_.FunctionName -match '^tick/\d' } | Select-Object -First 1
            if ($plFrame) {
                $dte.Debugger.CurrentStackFrame = $plFrame
                Start-Sleep -Seconds 2
                # Which frame VS thinks it is on. An empty Locals means one of two things --
                # the wrong frame is current, or the right one has no evaluator -- and only
                # this tells them apart.
                Write-Host "  current frame: $($dte.Debugger.CurrentStackFrame.FunctionName)"
                $locals = @($dte.Debugger.CurrentStackFrame.Locals) |
                    ForEach-Object { "$($_.Name) = $($_.Value)" }
                if ($locals.Count -gt 0) { $localsText = ($locals -join ", ") }
            }
        } catch { $localsText = "threw: $($_.Exception.Message)" }
    }
    Write-Host "  active document: $docName line $docLine"
    Write-Host "  locals of tick/2: $localsText"
    $results["D3-2 the stop opens the .pl at the right line"] =
        ($docName -eq $program -and $docLine -eq $bpLine)
    $results["D2-2 locals show the clause's variables"] =
        ($localsText -match 'N = ' -and $localsText -match 'Doubled = ')

    # --- D3-3: step. It has to reach a NEW stop, not merely leave the debugger in break
    # mode -- which it already is. The engine's stop count (from the diagnostic frame) is the
    # only thing that can tell those two apart.
    function Get-StopCount {
        $diag = @(Get-Frames | Where-Object { $_ -match '^\[Shumway diag\]' }) | Select-Object -First 1
        if ($diag -match 'stops=(\d+)') { return [int]$Matches[1] }
        return -1
    }

    Write-Host "[6/6] Step Into ..."
    $stepLine = 0
    $stepOk = $false
    if ($mode -eq 2) {
        try {
            $stopsBefore = Get-StopCount
            Invoke-WithRetry { $dte.Debugger.StepInto($false) } 10 2000
            Start-Sleep -Seconds 4
            if (Wait-ForBreak 20) {
                Select-PrologThread | Out-Null
                $stepLine = $dte.ActiveDocument.Selection.CurrentLine
                Show-Frames "call stack, after step" (Get-Frames)
                $stopsAfter = Get-StopCount
                Write-Host "  engine stops: $stopsBefore -> $stopsAfter"
                $stepOk = ($stopsAfter -gt $stopsBefore)
            }
        } catch { Write-Host "  StepInto threw: $($_.Exception.Message)" }
    }
    Write-Host "  after step, line: $stepLine"
    $results["D3-3 F11 steps to another port"] = $stepOk

    # --- D3-4: continue, and it is running again ---
    $ranOn = $false
    try {
        # Remove the breakpoint first, or Go stops at it again immediately.
        foreach ($bp in @($dte.Debugger.Breakpoints)) { $bp.Delete() }
        Invoke-WithRetry { $dte.Debugger.Go($false) } 10 2000
        Start-Sleep -Seconds 5
        $m3 = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 2000  # 3 = dbgRunMode
        $ranOn = ($m3 -eq 3)
        Write-Host "  debugger mode after continue: $m3 (3 = run)"
    } catch { Write-Host "  Go threw: $($_.Exception.Message)" }
    $results["D3-4 F5 continues"] = $ranOn

    Write-Host ""
    Write-Host "--- results ---"
    $allOk = $true
    foreach ($key in $results.Keys) {
        $ok = $results[$key]
        if (-not $ok) { $allOk = $false }
        Write-Host ("{0,-45} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
    }
    Write-Host ""
    if ($allOk) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL" }
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($dbgProc -and -not $dbgProc.HasExited) { $dbgProc.Kill() } } catch {}
}
