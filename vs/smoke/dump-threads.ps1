# Diagnostic: attach to a running shumway.exe --debug, Break All, and dump EVERY thread's
# stack verbatim. Nothing is asserted -- this is for looking.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl    = Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe"
$program = (Resolve-Path (Join-Path $PSScriptRoot "smoke.pl")).Path
$repl    = (Resolve-Path $repl).Path

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RotFinder2
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
            IBindCtx ctx; CreateBindCtx(0, out ctx);
            string name; m[0].GetDisplayName(ctx, null, out name);
            if (name.StartsWith("!VisualStudio.DTE.") && name.EndsWith(suffix))
            { object dte; rot.GetObject(m[0], out dte); return dte; }
        }
        return null;
    }
}
'@

function Invoke-WithRetry([scriptblock]$Action, [int]$Attempts = 30, [int]$DelayMs = 2000) {
    for ($i = 1; $i -le $Attempts; $i++) {
        try { return & $Action } catch { if ($i -eq $Attempts) { throw }; Start-Sleep -Milliseconds $DelayMs }
    }
}

$dbgProc = $null; $vsProc = $null
try {
    $env:SHUMWAY_GOAL = "main."
    $dbgProc = Start-Process -FilePath $repl -ArgumentList "--debug", "`"$program`"" -PassThru -WindowStyle Minimized
    Remove-Item Env:\SHUMWAY_GOAL
    Write-Host "debuggee pid $($dbgProc.Id)"

    $env:SHUMWAY_DEBUG_DIAG = "1"
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    Remove-Item Env:\SHUMWAY_DEBUG_DIAG
    $dte = Invoke-WithRetry { $d = [RotFinder2]::FindDte($vsProc.Id); if (-not $d) { throw "no DTE" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    $target = Invoke-WithRetry {
        $p = @($dte.Debugger.LocalProcesses) | Where-Object { $_.ProcessID -eq $dbgProc.Id }
        if (-not $p) { throw "not in LocalProcesses" }
        $p
    } 30 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Start-Sleep -Seconds 6

    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 4

    Write-Host ""
    Write-Host "=== every thread ==="
    foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
        Write-Host ""
        Write-Host ("--- thread {0} '{1}' ---" -f $t.ID, $t.Name)
        try {
            foreach ($f in @($t.StackFrames)) { Write-Host "    $($f.FunctionName)" }
        } catch { Write-Host "    <no frames: $($_.Exception.Message)>" }
    }
    Write-Host ""
    Write-Host "=== modules VS knows about (looking for our .pl) ==="
    try {
        foreach ($m in @($dte.Debugger.CurrentProgram.Process.Programs)) { Write-Host "  program: $($m.Name)" }
    } catch { Write-Host "  <programs: $($_.Exception.Message)>" }
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($dbgProc -and -not $dbgProc.HasExited) { $dbgProc.Kill() } } catch {}
}
