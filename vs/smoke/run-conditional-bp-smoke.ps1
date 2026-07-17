# ADR-035 D5 smoke check -- CONDITIONAL breakpoints, driven through Visual Studio.
#
# Launches shumway.exe --debug on smoke.pl (stderr captured, SHUMWAY_DEBUG_DIAG=1), starts
# devenv /rootsuffix Exp, attaches, and sets a breakpoint on tick/2's body WITH a Prolog
# condition ("0 =:= N mod 500"). Then asks the questions the manual smoke crashed on:
#
#   D5-1  the condition REACHES the engine (VS routes it: ParseCondition -> channel);
#         the engine's stderr shows per-hit "breakpoint condition ... -> fails/holds"
#         and NEVER "no condition attached"
#   D5-2  execution stops ONLY where the condition holds: at the stop, N mod 500 == 0
#   D5-3  it does it again: F5 -> the next stop also satisfies the condition, with a
#         larger N (the filtering is per-hit, not one-shot)
#   D5-4  the engine did the filtering (few notifies): stderr counts many "fails" per
#         "holds", and the component log shows the condition was parsed VS-side
#
# Run from Windows PowerShell 5.1 (COM ROT access). ASCII only -- PS 5.1 reads .ps1 as
# CP1252, where a UTF-8 em-dash decodes to a quote and silently terminates a string.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl    = Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe"
$program = Join-Path $PSScriptRoot "smoke.pl"

if (-not (Test-Path $repl))    { throw "build the REPL first (dotnet build src\Shumway.Repl): $repl not found" }
if (-not (Test-Path $program)) { throw "missing $program" }

$repl    = (Resolve-Path $repl).Path
$program = (Resolve-Path $program).Path

# The breakpoint line: the body of tick/2, the last non-empty line of the file. N is
# bound there, which is what the condition reads.
$lines = Get-Content $program
$bpLine = 0
for ($i = $lines.Count - 1; $i -ge 0; $i--) {
    if ($lines[$i].Trim().Length -gt 0) { $bpLine = $i + 1; break }
}
$condition = "0 =:= N mod 500"
Write-Host "breakpoint line in smoke.pl: $bpLine  ->  $($lines[$bpLine - 1].Trim())"
Write-Host "condition: $condition"

# The engine-side diagnostic goes to the debuggee's stderr; capture it.
$stderrLog = Join-Path $env:TEMP "shumway-condbp-stderr.log"
$stdoutLog = Join-Path $env:TEMP "shumway-condbp-stdout.log"
Remove-Item $stderrLog, $stdoutLog -ErrorAction SilentlyContinue

# The Concord components' log appends across runs; remember where this run starts.
$componentLog = Join-Path $env:TEMP "shumway-debug\component.log"
$componentStart = 0
if (Test-Path $componentLog) { $componentStart = (Get-Item $componentLog).Length }

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
    Write-Host "[1/6] starting shumway.exe --debug (stderr -> $stderrLog) ..."
    $env:SHUMWAY_GOAL = "main."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $dbgProc = Start-Process -FilePath $repl -ArgumentList "--debug", "`"$program`"" `
        -PassThru -NoNewWindow -RedirectStandardError $stderrLog -RedirectStandardOutput $stdoutLog
    Remove-Item Env:\SHUMWAY_GOAL
    Write-Host "  pid $($dbgProc.Id)"

    Write-Host "[2/6] starting devenv /rootsuffix Exp (first launch can be slow)..."
    # SHUMWAY_DEBUG_DIAG stays set: devenv inherits it -> component.log is written.
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

    # Reads N from the tick/2 frame's Locals at the current stop; -1 if unavailable.
    function Get-TickN {
        try {
            $plFrame = @($dte.Debugger.CurrentThread.StackFrames) |
                Where-Object { $_.FunctionName -match '(^|:)tick[(/!]' } | Select-Object -First 1
            if (-not $plFrame) { return -1 }
            $dte.Debugger.CurrentStackFrame = $plFrame
            Start-Sleep -Seconds 1
            $n = @($dte.Debugger.CurrentStackFrame.Locals) |
                Where-Object { $_.Name -eq "N" } | Select-Object -First 1
            if ($null -eq $n) { return -1 }
            return [long]$n.Value
        } catch { return -1 }
    }

    # The module dance: the IDE learns the .pl files at the first stop, the server builds
    # the modules at the next one. Same two-break bootstrap as the base smoke.
    Write-Host "[4/6] Break All twice (module bootstrap) ..."
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Start-Sleep -Seconds 4
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null

    Write-Host "[5/6] setting a CONDITIONAL breakpoint at smoke.pl:$bpLine ..."
    $bpAdded = $true
    try {
        # EnvDTE Breakpoints.Add(Function, File, Line, Column, Condition, ConditionType, ...)
        # ConditionType 1 = dbgBreakpointConditionTypeWhenTrue (EnvDTE enums are 1-based).
        Invoke-WithRetry {
            $dte.Debugger.Breakpoints.Add("", $program, $bpLine, 1, $condition, 1) | Out-Null
        } 10 2000
    }
    catch { $bpAdded = $false; Write-Host "  Breakpoints.Add threw: $($_.Exception.Message)" }
    $results["D5-0 conditional Breakpoints.Add succeeds"] = $bpAdded

    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Write-Host "  waiting for the conditional stop (evals pace the loop) ..."
    $stopped = Wait-ForBreak 90
    Write-Host "  stopped: $stopped"

    $n1 = -1
    if ($stopped) {
        Select-PrologThread | Out-Null
        $frames = Get-Frames
        Write-Host "  frames: $($frames -join ' | ')"
        $n1 = Get-TickN
        Write-Host "  N at stop #1: $n1"
    }
    $results["D5-2 stops only where the condition holds"] =
        ($stopped -and $n1 -gt 0 -and ($n1 % 500) -eq 0)

    Write-Host "[6/6] F5 -> the NEXT conditional stop ..."
    $n2 = -1
    if ($stopped) {
        Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
        if (Wait-ForBreak 90) {
            Select-PrologThread | Out-Null
            $n2 = Get-TickN
            Write-Host "  N at stop #2: $n2"
        }
    }
    $results["D5-3 stops again, later, condition holding"] =
        ($n2 -gt $n1 -and ($n2 % 500) -eq 0)
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($dbgProc -and -not $dbgProc.HasExited) { $dbgProc.Kill() } } catch {}
}

# --- the logs: who actually did the filtering ---
Start-Sleep -Seconds 2

$stderr = @()
if (Test-Path $stderrLog) { $stderr = Get-Content $stderrLog }
$fails      = @($stderr | Where-Object { $_ -match "breakpoint condition .* fails \(run on\)" })
$holds      = @($stderr | Where-Object { $_ -match "breakpoint condition .* holds \(stop\)" })
$errors     = @($stderr | Where-Object { $_ -match "breakpoint condition .* ERROR" })
$noCond     = @($stderr | Where-Object { $_ -match "no condition attached" })
Write-Host ""
Write-Host "=== engine stderr (tail) ==="
$stderr | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" }
Write-Host "  [counts] fails=$($fails.Count) holds=$($holds.Count) errors=$($errors.Count) no-condition=$($noCond.Count)"

$results["D5-1 the condition reached the engine"] =
    ($fails.Count -gt 0 -and $holds.Count -ge 1 -and $noCond.Count -eq 0)
$results["D5-4 the engine filtered (many fails per hold)"] =
    ($holds.Count -ge 1 -and $fails.Count -ge $holds.Count)

$parsedVsSide = $false
if (Test-Path $componentLog) {
    $stream = [System.IO.File]::Open($componentLog, 'Open', 'Read', 'ReadWrite')
    try {
        $stream.Position = $componentStart
        $reader = New-Object System.IO.StreamReader($stream)
        $newLog = $reader.ReadToEnd() -split "`r?`n"
    } finally { $stream.Dispose() }
    $parsed = @($newLog | Where-Object { $_ -match "condition parsed for bp" })
    $parsedVsSide = ($parsed.Count -gt 0)
    Write-Host ""
    Write-Host "=== component.log: condition routing ==="
    if ($parsedVsSide) { $parsed | Select-Object -First 3 | ForEach-Object { Write-Host "  $_" } }
    else { Write-Host "  (no 'condition parsed for bp' line -- VS never routed the condition to us)" }
}
$results["D5-4b VS-side ParseCondition fired"] = $parsedVsSide

Write-Host ""
Write-Host "--- results ---"
$allOk = $true
foreach ($key in $results.Keys) {
    $ok = $results[$key]
    if (-not $ok) { $allOk = $false }
    Write-Host ("{0,-50} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
}
Write-Host ""
if ($allOk) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL" }
