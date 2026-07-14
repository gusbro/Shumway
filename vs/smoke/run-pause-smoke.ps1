# ADR-035 -- BREAK ALL ON A PROGRAM THAT IS ACTUALLY RUNNING.
#
# The user's report: `shumway --debug blint.pl`, run the long goal, press Break All -- and
# Visual Studio hangs on "breaking" and never stops. Every smoke we had pauses an engine that
# is standing STILL (at the prompt), which is the easy half: the idle watcher grants that stop.
# Nothing covered the half the pause exists for.
#
#   P1  the engine is running, Break All stops it -- at a PORT, with a real Prolog stack
#   P2  and F5 lets it run on to the answer
#
# Run from Windows PowerShell 5.1. ASCII only.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl    = (Resolve-Path (Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe")).Path
$program = (Resolve-Path (Join-Path $PSScriptRoot "pause-spin.pl")).Path

$DeadlineSeconds = 420
$deadline = (Get-Date).AddSeconds($DeadlineSeconds)
function Assert-Time([string]$what) {
    if ((Get-Date) -gt $deadline) { throw "DEADLINE ($DeadlineSeconds s) reached while: $what" }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RotFinder7
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
    Write-Host "[1/5] shumway --debug pause-spin.pl ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $repl
    $psi.Arguments = "--debug `"$program`""
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $engine = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 3

    Write-Host "[2/5] devenv, and attach ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    $dte = Invoke-WithRetry { $d = [RotFinder7]::FindDte($vsProc.Id); if (-not $d) { throw "no DTE" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10
    $target = Invoke-WithRetry {
        if ($engine.HasExited) { throw "the engine exited" }
        $all = @($dte.Debugger.LocalProcesses)
        $p = $all | Where-Object { $_.ProcessID -eq $engine.Id }
        if (-not $p) { throw "engine pid $($engine.Id) not among $($all.Count) processes yet" }
        $p
    } 45 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Start-Sleep -Seconds 8

    Write-Host "[3/5] starting the long goal ..."
    $engine.StandardInput.WriteLine("go.")
    $engine.StandardInput.Flush()
    Start-Sleep -Seconds 4   # let it get properly under way

    Write-Host "[4/5] Break All -- WHILE IT RUNS ..."
    $paused = $false
    try {
        Invoke-WithRetry { $dte.Debugger.Break($false) } 3 2000
        for ($i = 0; $i -lt 25; $i++) {
            Assert-Time "waiting for the break to land"
            Start-Sleep -Seconds 1
            if ((Invoke-WithRetry { $dte.Debugger.CurrentMode } 5 1000) -eq 2) { $paused = $true; break }
        }
    } catch { Write-Host "  Break All threw: $($_.Exception.Message)" }
    Write-Host "  paused: $paused"

    $frames = @()
    if ($paused) {
        $frames = Invoke-WithRetry {
            $out = @()
            foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
                $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
                if (@($names | Where-Object { $_ -match '^(\w+/-?\d+|\?- )' }).Count -gt 0) {
                    $dte.Debugger.CurrentThread = $t
                    $out = $names
                    break
                }
            }
            $out
        } 5 2000
        Write-Host "=== stack at the pause ==="
        $frames | Select-Object -First 6 | ForEach-Object { Write-Host "   $_" }
        Write-Host "=========================="
    }
    # A pause with no Prolog stack is not a pause, it is a freeze: the whole point is that the
    # engine stops at a PORT, where a stack exists.
    $results["P1 Break All stops a RUNNING engine, at a port"] =
        ($paused -and @($frames | Where-Object { $_ -match '^(spin|work|go)/' }).Count -gt 0)

    Write-Host "[5/5] F5, and let it finish ..."
    $ranOn = $false
    if ($paused) {
        try {
            Invoke-WithRetry { $dte.Debugger.Go($false) } 5 2000
            for ($i = 0; $i -lt 20; $i++) {
                Start-Sleep -Seconds 1
                if ((Invoke-WithRetry { $dte.Debugger.CurrentMode } 5 1000) -ne 2) { $ranOn = $true; break }
            }
        } catch { Write-Host "  Go threw: $($_.Exception.Message)" }
    }
    $results["P2 F5 lets it run on"] = $ranOn

    if (Test-Path $componentLog) {
        Write-Host ""
        Write-Host "--- component log (last 12) ---"
        Get-Content $componentLog | Select-Object -Last 12 | ForEach-Object { Write-Host "   $_" }
    }

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
