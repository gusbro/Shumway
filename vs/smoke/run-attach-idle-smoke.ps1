# ADR-035 -- attaching to an engine that is standing still, which is how anyone actually
# debugs a program they did not launch from the IDE.
#
#   shumway --debug prog.pl        -> the engine consults it and waits at the prompt
#   attach from Visual Studio      -> nothing has stopped, and nothing is going to
#   open prog.pl, press F9         -> "no symbols have been loaded for this document"
#
# That was a real deadlock: a breakpoint binds against a module, a module can only be built
# inside a stop, and a stop can only come from a breakpoint. Nobody could go first. The
# engine now grants the debugger's bootstrap stop even when it is idle (ChannelDebugSession's
# idle watcher), and the component asks for it the moment it attaches.
#
#   A1  a breakpoint set on an IDLE engine binds -- and is HIT when the goal finally runs
#   A2  debugger_break/0 stops the debugger by itself, with the clause's own stack
#
# Run from Windows PowerShell 5.1. ASCII only.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl    = Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe"
$program = Join-Path $PSScriptRoot "attach-idle.pl"

foreach ($f in @($repl, $program)) { if (-not (Test-Path $f)) { throw "missing $f" } }
$repl    = (Resolve-Path $repl).Path
$program = (Resolve-Path $program).Path

# THE ENGINE IS GIVEN THE PATH THE WAY A USER TYPES IT -- lower-case drive letter -- while
# Visual Studio opens the document with the drive the way Windows reports it. Those were two
# different files to the engine's site table, so a breakpoint bound against the one with no
# code in it and NEVER HIT: the program ran clean through it and the debugger looked broken.
# The smoke passed only because it handed the engine a resolved path, which is not what
# anybody does. It hands it a badly-cased one now.
$programAsTyped = $program.Substring(0, 1).ToLowerInvariant() + $program.Substring(1)

$tracefile = Join-Path $PSScriptRoot "attach-idle-trace.txt"
Remove-Item $tracefile -ErrorAction SilentlyContinue

$lines = Get-Content $program
$bpLine = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*Out is N \* 2') { $bpLine = $i + 1; break }
}
if ($bpLine -eq 0) { throw "could not find the body of step/2" }
Write-Host "breakpoint: attach-idle.pl:$bpLine   ($($lines[$bpLine-1].Trim()))"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RotFinder5
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

$vsProc = $null
$engine = $null
$results = [ordered]@{}

try {
    Write-Host "[1/6] starting shumway --debug (it will sit at the prompt) ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    # stdin is redirected so the smoke can type the goal AFTER the breakpoint is set --
    # which is the whole point: the engine must be IDLE while the debugger gets ready.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $repl
    $psi.Arguments = "--debug `"$programAsTyped`""
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $engine = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 3
    Write-Host "  engine pid $($engine.Id), idle at the prompt"

    Write-Host "[2/6] starting devenv /rootsuffix Exp ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    $dte = Invoke-WithRetry { $d = [RotFinder5]::FindDte($vsProc.Id); if (-not $d) { throw "no DTE yet" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    Write-Host "[3/6] attaching to the idle engine ..."
    $target = Invoke-WithRetry {
        if ($engine.HasExited) { throw "the engine exited (code $($engine.ExitCode))" }
        $all = @($dte.Debugger.LocalProcesses)
        $p = $all | Where-Object { $_.ProcessID -eq $engine.Id }
        if (-not $p) { throw "engine pid $($engine.Id) not among $($all.Count) local processes yet" }
        $p
    } 45 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Start-Sleep -Seconds 6   # the idle watcher grants the bootstrap stop within ~100ms

    # BREAK ALL WITH NOTHING RUNNING. The engine is sitting at its prompt; no port is coming.
    # The pause used to be DECLINED here (a thrown NotImplementedException, the way a stepper
    # declines) -- and there is no fallback for that: Visual Studio put up "Unable to break
    # execution. Not implemented" and the user got a dialog instead of a debugger. The engine
    # grants a stop when it is idle, so there was never anything to decline.
    Write-Host "[3b/6] Break All on an IDLE engine ..."
    $pausedIdle = $false
    try {
        Invoke-WithRetry { $dte.Debugger.Break($false) } 3 2000
        for ($i = 0; $i -lt 15; $i++) {
            Start-Sleep -Seconds 1
            if ((Invoke-WithRetry { $dte.Debugger.CurrentMode } 5 1000) -eq 2) { $pausedIdle = $true; break }
        }
        Write-Host "  paused: $pausedIdle"
        if ($pausedIdle) { Invoke-WithRetry { $dte.Debugger.Go($false) } 5 2000; Start-Sleep -Seconds 2 }
    } catch { Write-Host "  Break All threw: $($_.Exception.Message)" }
    $results["A0 Break All on an idle engine pauses (no 'Not implemented')"] = $pausedIdle

    Write-Host "[4/6] opening the file and setting a breakpoint -- ON AN IDLE ENGINE ..."
    Invoke-WithRetry {
        $ops = $dte.ItemOperations
        if ($null -eq $ops) { throw "the shell is not ready" }
        $ops.OpenFile($program) | Out-Null
    } 30 2000
    Start-Sleep -Seconds 3
    Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $program, $bpLine) | Out-Null } 10 2000
    Start-Sleep -Seconds 3

    function Wait-ForBreak([int]$Seconds = 40) {
        for ($i = 0; $i -lt $Seconds; $i++) {
            $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
            if ($m -eq 2) { return $true }
            Start-Sleep -Seconds 1
        }
        return $false
    }
    function Prolog-Frames {
        Invoke-WithRetry {
            $chosen = $null
            foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
                $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
                if (@($names | Where-Object { $_ -match '^\w+/\d+$' }).Count -gt 0) { $chosen = $t; break }
            }
            if (-not $chosen) { throw "no thread with Prolog frames yet" }
            $dte.Debugger.CurrentThread = $chosen
            @($chosen.StackFrames) | ForEach-Object { $_.FunctionName }
        } 10 2000
    }

    Write-Host "[5/6] NOW running the goal -- the breakpoint was set before it existed ..."
    $engine.StandardInput.WriteLine("go.")
    $engine.StandardInput.Flush()

    $hit = Wait-ForBreak 40
    $frames = @()
    if ($hit) {
        $frames = Prolog-Frames
        Write-Host ""
        Write-Host "=== stack at the breakpoint ==="
        $frames | ForEach-Object { Write-Host "  $_" }
        Write-Host "==============================="
    }
    $results["A1 a breakpoint set on an IDLE engine binds and hits"] =
        ($hit -and @($frames | Where-Object { $_ -match '^step/2$' }).Count -ge 1)

    Write-Host "[6/6] debugger_break/0 ..."
    $brk = $false
    $brkFrames = @()
    try {
        foreach ($bp in @($dte.Debugger.Breakpoints)) { $bp.Delete() }
        Invoke-WithRetry { $dte.Debugger.Go($false) } 10 2000
        Start-Sleep -Seconds 2
        $engine.StandardInput.WriteLine("marked.")
        $engine.StandardInput.Flush()
        $brk = Wait-ForBreak 30
        if ($brk) {
            $brkFrames = Prolog-Frames
            Write-Host ""
            Write-Host "=== stack at debugger_break/0 ==="
            $brkFrames | ForEach-Object { Write-Host "  $_" }
            Write-Host "================================="
        }
    } catch { Write-Host "  debugger_break threw: $($_.Exception.Message)" }
    $results["A2 debugger_break/0 stops, with its own stack"] =
        ($brk -and @($brkFrames | Where-Object { $_ -match '^marked/0$' }).Count -ge 1)

    # A stop you cannot step from is half a debugger. This failed with "Unable to step.
    # Operation not supported" -- the component knew about the stops that came through its
    # own breakpoint and no others, so it declined a step it should have taken, and the CLR
    # was left trying to step a Prolog frame that is not its code.
    $stepped = $false
    if ($brk) {
        try {
            Invoke-WithRetry { $dte.Debugger.StepOver($true) } 5 2000
            Start-Sleep -Seconds 2
            $after = Prolog-Frames
            Write-Host "  after F10: $($after[0])"
            $stepped = ($dte.Debugger.CurrentMode -eq 2)
        } catch { Write-Host "  F10 threw: $($_.Exception.Message)" }
    }
    $results["A3 you can STEP from a debugger_break stop"] = $stepped

    Write-Host ""
    # A program that STOPPED and one that never ran look identical from the IDE. The trace
    # says which: the goals leave a mark as they complete.
    $trace = (Get-Content $tracefile -ErrorAction SilentlyContinue) -join " | "
    Write-Host "--- what the program actually did: '$trace' ---"

    Write-Host "--- results ---"
    $allOk = $true
    foreach ($key in $results.Keys) {
        $ok = $results[$key]
        if (-not $ok) { $allOk = $false }
        Write-Host ("{0,-52} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
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
