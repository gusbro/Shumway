# ADR-035 Phase D0 spike check (leg 4: stack filter end-to-end on VS 2026).
# Launches SpikeDebuggee + devenv /rootsuffix Exp, attaches the managed debugger
# via DTE COM automation, breaks, and dumps the call stack. PASS = synthesized
# "[Prolog]" frames appear in place of the SpikeDebuggee Dispatch frame.
#
# Run from Windows PowerShell 5.1 (COM ROT access). First Exp-hive launch of
# devenv can take a couple of minutes.

$ErrorActionPreference = 'Stop'

$devenv   = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$debuggee = Join-Path $PSScriptRoot "SpikeDebuggee\bin\Debug\net10.0\SpikeDebuggee.exe"
if (-not (Test-Path $debuggee)) { throw "Build the spike solution first: $debuggee not found" }

# --- ROT helper: find the DTE object for a specific devenv PID -----------------
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
try {
    Write-Host "[1/5] starting SpikeDebuggee..."
    $dbgProc = Start-Process -FilePath $debuggee -PassThru -WindowStyle Hidden

    Write-Host "[2/5] starting devenv /rootsuffix Exp (first launch can be slow)..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru

    Write-Host "[3/5] waiting for DTE in the running object table..."
    $dte = Invoke-WithRetry { $d = [RotFinder]::FindDte($vsProc.Id); if ($null -eq $d) { throw "no DTE yet" }; $d } 90 2000
    # Let the IDE finish initializing before driving the debugger.
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    Write-Host "[4/5] attaching managed debugger to SpikeDebuggee pid=$($dbgProc.Id)..."
    $target = Invoke-WithRetry {
        $p = @($dte.Debugger.LocalProcesses) | Where-Object { $_.ProcessID -eq $dbgProc.Id }
        if (-not $p) { throw "debuggee not in LocalProcesses yet" }
        $p
    } 30 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Start-Sleep -Seconds 5

    Write-Host "[5/5] Break All + dumping call stack..."
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3

    $frames = Invoke-WithRetry {
        $f = @($dte.Debugger.CurrentThread.StackFrames) | ForEach-Object { $_.FunctionName }
        if (-not $f) { throw "no frames yet" }
        $f
    } 10 2000

    Write-Host ""
    Write-Host "=== call stack ==="
    $frames | ForEach-Object { Write-Host "  $_" }
    Write-Host "=================="

    $prolog   = @($frames | Where-Object { $_ -like "*Prolog*" })
    $physical = @($frames | Where-Object { $_ -like "*BytecodeInterpreter.Dispatch*" })
    if ($prolog.Count -ge 2 -and $physical.Count -eq 0) {
        Write-Host "RESULT: PASS - $($prolog.Count) synthesized [Prolog] frames, physical Dispatch frame replaced."
    } elseif ($prolog.Count -gt 0) {
        Write-Host "RESULT: PARTIAL - [Prolog] frames present but Dispatch also visible ($($physical.Count))."
    } else {
        Write-Host "RESULT: FAIL - no [Prolog] frames; filter did not run or did not match."
    }
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($dbgProc -and -not $dbgProc.HasExited) { $dbgProc.Kill() } } catch {}
}
