# ADR-035 -- the LINKED --exe --debug-wait path, attached by hand.
#
# The user's scenario, which no existing smoke covers: a program built with
#   shumway-compile --debug ... ; shumway-link --exe --debug-wait ...
# is a SINGLE-FILE bundle (the engine DLLs are embedded, not on disk), and the user attaches
# to it by hand rather than launching it from the VSIX. Reported broken: after attach it either
# ran past the entry point, or stopped showing the C# engine stack (not Prolog), and Break All
# said "not implemented".
#
#   E1  after attach, the program STOPS at the entry point (does not run to completion)
#   E2  the stack at that stop is the PROLOG stack (main/step/show), not the C# engine
#   E3  Break All works (no "not implemented")
#
# Run from Windows PowerShell 5.1. ASCII only.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$work    = Join-Path $PSScriptRoot "exe-debug-work"
$compile = Join-Path $PSScriptRoot "..\..\src\Shumway.Compile\bin\x64\Release\net10.0\shumway-compile.exe"
$link    = Join-Path $PSScriptRoot "..\..\src\Shumway.Link\bin\x64\Release\net10.0\shumway-link.exe"

foreach ($f in @($devenv, $compile, $link)) { if (-not (Test-Path $f)) { throw "missing $f" } }

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work | Out-Null
$pl = Join-Path $work "entry.pl"
@'
:- public main/0.
main :-
    step(1, A),
    step(A, B),
    show(B).
step(N, Out) :- Out is N * 2.
show(X) :- write(result(X)), nl.
'@ | Out-File -FilePath $pl -Encoding ascii

Write-Host "[0/6] compile --debug + link --exe --debug-wait ..."
& $compile --debug $pl -o (Join-Path $work "entry.shmo")
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
& $link (Join-Path $work "entry.shmo") --goal main --exe (Join-Path $work "entryapp") --debug-wait
if ($LASTEXITCODE -ne 0) { throw "link failed" }
$exe = Join-Path $work "entryapp.exe"
if (-not (Test-Path $exe)) { throw "no exe produced" }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RotFinderExe
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
    Write-Host "[1/6] starting the exe (it will wait for attach) ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $engine = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 2
    Write-Host "  exe pid $($engine.Id), waiting for attach"

    Write-Host "[2/6] starting devenv /rootsuffix Exp ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    $dte = Invoke-WithRetry { $d = [RotFinderExe]::FindDte($vsProc.Id); if (-not $d) { throw "no DTE yet" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    Write-Host "[3/6] attaching to the waiting exe ..."
    $target = Invoke-WithRetry {
        if ($engine.HasExited) { throw "the exe exited (code $($engine.ExitCode)) before attach" }
        $all = @($dte.Debugger.LocalProcesses)
        $p = $all | Where-Object { $_.ProcessID -eq $engine.Id }
        if (-not $p) { throw "exe pid $($engine.Id) not among $($all.Count) local processes yet" }
        $p
    } 45 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Write-Host "  attached"

    Write-Host "[4/6] waiting for the entry stop (CurrentMode 2 = break) ..."
    $stopped = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 1
        $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 5 1000
        if ($m -eq 2) { $stopped = $true; break }
        if ($engine.HasExited) { break }
    }
    Write-Host "  stopped at entry: $stopped (exe exited: $($engine.HasExited))"
    $results["E1 after attach the program STOPS at the entry point"] = $stopped

    $frames = @()
    if ($stopped) {
        $chosen = $null
        foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
            $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
            if (@($names | Where-Object { $_ -match '^\w+/\d+$' -or $_ -match '!\d+$' -or $_ -match '^\[Shumway' }).Count -gt 0) { $chosen = $t; break }
        }
        if ($chosen) { $dte.Debugger.CurrentThread = $chosen; Start-Sleep -Seconds 1 }
        $frames = @($dte.Debugger.CurrentThread.StackFrames) | ForEach-Object { $_.FunctionName }
        Write-Host ""
        Write-Host "=== stack at the entry stop ==="
        $frames | ForEach-Object { Write-Host "  $_" }
        Write-Host "==============================="
    }
    $prolog = @($frames | Where-Object { $_ -match '(^|:)(main|step|show)([(/!]|$)' })
    $results["E2 the stack at the entry stop is the PROLOG stack"] = ($prolog.Count -ge 1)

    Write-Host "[5/6] Break All (after a Continue) ..."
    $broke = $false
    try {
        Invoke-WithRetry { $dte.Debugger.Go($false) } 5 2000
        Start-Sleep -Seconds 1
        Invoke-WithRetry { $dte.Debugger.Break($false) } 3 2000
        for ($i = 0; $i -lt 15; $i++) {
            Start-Sleep -Seconds 1
            if ((Invoke-WithRetry { $dte.Debugger.CurrentMode } 5 1000) -eq 2) { $broke = $true; break }
        }
    } catch { Write-Host "  Break All threw: $($_.Exception.Message)" }
    Write-Host "  Break All paused: $broke"
    $results["E3 Break All works (no 'not implemented')"] = $broke

    Write-Host "[6/6] diagnostics from the exe ..."
    try { if (-not $engine.HasExited) { $engine.Kill() } } catch {}
    Start-Sleep -Milliseconds 500
    $err = $engine.StandardError.ReadToEnd()
    Write-Host "--- exe stderr ---"
    Write-Host $err

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
    Get-Process entryapp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
