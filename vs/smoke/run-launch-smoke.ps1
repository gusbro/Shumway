# ADR-035 D4 smoke -- the LAUNCH path (no attach by hand).
#
# The D2/D3 smoke attached to a program that was already running. That proved the debugger
# works; it did not prove anyone can USE it. This one asks the question a user asks:
#
#   D4-1  the package loads and the command exists
#   D4-2  the command LAUNCHES shumway.exe on the open .pl under the debugger
#   D4-3  a breakpoint set BEFORE the launch is hit -- which is only possible because
#         --debug-wait holds the engine at the door until the debugger is attached
#   D4-4  the Prolog stack is there at that stop (the components loaded from the VSIX,
#         not from a hand-deployed folder)
#   D4-5  the program runs to completion after Continue, and the session ends
#
# Run from Windows PowerShell 5.1 (COM ROT access). Keep this file ASCII-only -- PS 5.1
# reads .ps1 as CP1252, where a UTF-8 em-dash decodes to a quote and silently terminates
# a string literal mid-line.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl    = Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe"
$program = Join-Path $PSScriptRoot "launch.pl"

if (-not (Test-Path $repl))    { throw "build the REPL first (dotnet build src\Shumway.Repl): $repl not found" }
if (-not (Test-Path $program)) { throw "missing $program" }

$repl    = (Resolve-Path $repl).Path
$program = (Resolve-Path $program).Path

# The breakpoint: the body of tick/2, the last non-empty line.
$lines = Get-Content $program
$bpLine = 0
for ($i = $lines.Count - 1; $i -ge 0; $i--) {
    if ($lines[$i].Trim().Length -gt 0) { $bpLine = $i + 1; break }
}
Write-Host "breakpoint line in launch.pl: $bpLine  ->  $($lines[$bpLine - 1].Trim())"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class RotFinder2
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

$vsProc = $null
$results = [ordered]@{}

try {
    Write-Host "[1/5] starting devenv /rootsuffix Exp ..."
    # How the package finds the engine. The options page comes first, the environment second,
    # PATH third -- and a smoke run should not depend on a setting a human left behind.
    $env:SHUMWAY_EXE = $repl
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    $dte = Invoke-WithRetry { $d = [RotFinder2]::FindDte($vsProc.Id); if ($null -eq $d) { throw "no DTE yet" }; $d } 90 2000
    if ($null -eq $dte) { throw "the ROT gave us nothing for devenv pid $($vsProc.Id)" }
    Write-Host "  dte: $($dte.Version) (pid $($vsProc.Id))"
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    Write-Host "[2/5] opening launch.pl and looking for the command ..."
    # ItemOperations is null until the shell has finished coming up -- retry on the PROPERTY,
    # not just on the call, or the first attempt dereferences nothing and the script dies.
    Invoke-WithRetry {
        $ops = $dte.ItemOperations
        if ($null -eq $ops) { throw "the shell is not ready (no ItemOperations yet)" }
        $ops.OpenFile($program) | Out-Null
    } 30 2000
    Start-Sleep -Seconds 3

    # By (guid, id), NOT by canonical name. VS renames a command after the menu it was placed
    # on -- ours answers to "EditorContextMenus.CodeWindow.Shumway.DebugPrologFile", which is
    # not a name anybody would guess, and ExecuteCommand on the name we gave it fails.
    $commandSet = "{c74e33bf-2316-41cf-a971-b3bc83745619}"
    $commandId = 0x0100
    $command = $null
    try { $command = $dte.Commands.Item($commandSet, $commandId) } catch { }
    $results["D4-1 the package loads and the command exists"] = ($null -ne $command)
    if ($command) { Write-Host "  command: '$($command.Name)' available=$($command.IsAvailable)" }
    else { Write-Host "  command NOT registered" }

    Write-Host "[3/5] setting a breakpoint at launch.pl:$bpLine (BEFORE the launch) ..."
    Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $program, $bpLine) | Out-Null } 10 2000

    Write-Host "[4/5] invoking the command ..."
    $launched = $false
    try {
        # ExecuteCommand takes a NAME, and the name is whatever VS decided it is (see above) --
        # so ask the command for it rather than assuming.
        Invoke-WithRetry { $dte.ExecuteCommand($command.Name) } 5 2000
        $launched = $true
    } catch { Write-Host "  ExecuteCommand threw: $($_.Exception.Message)" }
    # Did anything actually start? The three questions, in order: is there a process, did it
    # open a debug session (the channel file it publishes is the proof), and is the IDE
    # debugging it. A launch that quietly did nothing looks like a breakpoint that did not
    # bind, and they are not the same bug.
    Start-Sleep -Seconds 4
    $engineProcs = @(Get-Process -Name shumway -ErrorAction SilentlyContinue)
    Write-Host "  shumway processes: $($engineProcs.Count) $(($engineProcs | ForEach-Object { $_.Id }) -join ',')"
    $chanDir = Join-Path ([IO.Path]::GetTempPath()) "shumway-debug"
    if (Test-Path $chanDir) {
        Get-ChildItem $chanDir -File | ForEach-Object {
            Write-Host "  channel: $($_.Name) -> $((Get-Content $_.FullName -Raw).Trim())"
        }
    } else { Write-Host "  no channel directory: the engine never opened a debug session" }
    try { Write-Host "  debugger mode: $($dte.Debugger.CurrentMode)  (1 design, 2 break, 3 run)" } catch {}

    $results["D4-2 the command launches the engine"] = ($launched -and $engineProcs.Count -ge 1)

    # dbgDebugMode: 1 = design, 2 = break, 3 = run.
    $stopped = $false
    for ($i = 0; $i -lt 40; $i++) {
        $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
        if ($m -eq 2) { $stopped = $true; break }
        Start-Sleep -Seconds 1
    }
    $results["D4-3 a breakpoint set before the launch is hit"] = $stopped
    Write-Host "  stopped at the breakpoint: $stopped"

    if (-not $stopped) {
        # It never stopped and it never ended. Break in and ask where it is -- the diagnostic
        # frame carries what the server managed to do (SHUMWAY_DEBUG_DIAG=1).
        Write-Host "  (breaking in to see where it got to)"
        try {
            $dte.Debugger.Break($true)
            Start-Sleep -Seconds 3
            foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
                $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
                if (@($names | Where-Object { $_ -match '^\[Shumway|^\w+/\d+$|Shumway\.' }).Count -gt 0) {
                    Write-Host "  --- thread $($t.ID) ---"
                    $names | ForEach-Object { Write-Host "    $_" }
                }
            }
        } catch { Write-Host "  break-in threw: $($_.Exception.Message)" }
    }

    $frames = @()
    if ($stopped) {
        # The thread running the query is the one with Prolog on it (see run-smoke.ps1: a
        # -like pattern would treat [Shumway] as a character class and pick the wrong one).
        $chosen = $null
        foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
            $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
            if (@($names | Where-Object { $_ -match '^\w+/\d+$' -or $_ -match '^\[Shumway' }).Count -gt 0) {
                $chosen = $t; break
            }
        }
        if ($chosen) { $dte.Debugger.CurrentThread = $chosen; Start-Sleep -Seconds 2 }
        $frames = @($dte.Debugger.CurrentThread.StackFrames) | ForEach-Object { $_.FunctionName }
        Write-Host ""
        Write-Host "=== call stack, at the breakpoint ==="
        $frames | ForEach-Object { Write-Host "  $_" }
        Write-Host "===================================="
    }
    $prolog = @($frames | Where-Object { $_ -match "^(main|tick)/\d" })
    $results["D4-4 the Prolog stack is there"] = ($prolog.Count -ge 1)

    Write-Host "[5/5] Continue, and let it finish ..."
    $finished = $false
    try {
        foreach ($bp in @($dte.Debugger.Breakpoints)) { $bp.Delete() }
        Invoke-WithRetry { $dte.Debugger.Go($false) } 10 2000
        for ($i = 0; $i -lt 30; $i++) {
            $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
            if ($m -eq 1) { $finished = $true; break }   # design mode = the debuggee exited
            Start-Sleep -Seconds 1
        }
    } catch { Write-Host "  Go threw: $($_.Exception.Message)" }
    $results["D4-5 the program runs to completion"] = $finished
    Write-Host "  debuggee exited: $finished"

    Write-Host ""
    Write-Host "--- results ---"
    $allOk = $true
    foreach ($key in $results.Keys) {
        $ok = $results[$key]
        if (-not $ok) { $allOk = $false }
        Write-Host ("{0,-48} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
    }
    Write-Host ""
    if ($allOk) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL" }
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    Get-Process -Name shumway -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
