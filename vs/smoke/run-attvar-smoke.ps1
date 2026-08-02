# ADR-035 attvar-residuals smoke -- constraints of attributed variables in VS Locals.
#
# Launches shumway.exe --debug on attvar.pl (a clpfd loop), attaches through devenv Exp,
# breaks at the mark/3 goal of loop/1, and asks the one question this feature exists to
# answer:
#
#   R-1  the loop frame's Locals carry a "<var> <constraints>" row whose value shows the
#        projected domain (X in 1..6, X#<Y)
#
# Keep this file ASCII-only -- PS 5.1 reads .ps1 as CP1252. The constraints row's NAME
# contains a non-ASCII angle bracket, so it is matched with -match 'constraints', never
# spelled out here.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl    = Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe"
$program = Join-Path $PSScriptRoot "attvar.pl"

if (-not (Test-Path $repl))    { throw "build the REPL first (dotnet build src\Shumway.Repl): $repl not found" }
if (-not (Test-Path $program)) { throw "missing $program" }

$repl    = (Resolve-Path $repl).Path
$program = (Resolve-Path $program).Path

# The breakpoint line: the mark/3 goal inside loop/1.
$lines = Get-Content $program
$bpLine = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'mark\(X, Y, N\)') { $bpLine = $i + 1; break }
}
if ($bpLine -eq 0) { throw "attvar.pl has no mark(X, Y, N) goal line" }
Write-Host "breakpoint line in attvar.pl: $bpLine  ->  $($lines[$bpLine - 1].Trim())"

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
    Write-Host "[1/5] starting shumway.exe --debug ..."
    $env:SHUMWAY_GOAL = "main."
    $dbgProc = Start-Process -FilePath $repl -ArgumentList "--debug", "`"$program`"" `
        -PassThru -WindowStyle Minimized
    Remove-Item Env:\SHUMWAY_GOAL
    Write-Host "  pid $($dbgProc.Id)"

    Write-Host "[2/5] starting devenv /rootsuffix Exp ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    $dte = Invoke-WithRetry { $d = [RotFinder]::FindDte($vsProc.Id); if ($null -eq $d) { throw "no DTE yet" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    Write-Host "[3/5] attaching to shumway.exe ..."
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
            if ($dte.Debugger.CurrentThread.ID -ne $chosen.ID) { throw "CurrentThread did not take" }
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

    # Two breaks: the first teaches the IDE the .pl files; modules appear at the next
    # pause (same choreography as run-smoke.ps1).
    Write-Host "[4/5] Break All twice (module bootstrap) ..."
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Start-Sleep -Seconds 4
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Start-Sleep -Seconds 2

    Write-Host "[5/5] breakpoint at attvar.pl:$bpLine, then read Locals ..."
    Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $program, $bpLine) | Out-Null } 10 2000
    $stopped = Wait-ForBreak 30
    Write-Host "  stopped at the breakpoint: $stopped"
    $results["R-0 the breakpoint stops"] = $stopped

    $localsText = "(none)"
    if ($stopped) {
        Select-PrologThread | Out-Null
        try {
            $plFrame = @($dte.Debugger.CurrentThread.StackFrames) |
                Where-Object { $_.FunctionName -match '(^|:)loop[(/!]' } | Select-Object -First 1
            if ($plFrame) {
                $dte.Debugger.CurrentStackFrame = $plFrame
                Start-Sleep -Seconds 2
                $locals = @($dte.Debugger.CurrentStackFrame.Locals) |
                    ForEach-Object { "$($_.Name) = $($_.Value)" }
                if ($locals.Count -gt 0) { $localsText = ($locals -join "; ") }
            }
        } catch { $localsText = "threw: $($_.Exception.Message)" }
    }
    Write-Host "  locals of loop/1: $localsText"
    # The row name carries the angle-bracketed suffix; the value is the projection.
    $results["R-1 locals carry the constraints rows"] =
        ($localsText -match 'X [^ ]*constraints[^ ]* = X in' -and $localsText -match 'Y [^ ]*constraints[^ ]* = Y in')

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
