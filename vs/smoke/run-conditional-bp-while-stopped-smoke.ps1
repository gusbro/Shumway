# ADR-035 D5 smoke check -- the user's EXACT flow, which the other two smokes do not cover:
#
#   1. unconditional breakpoint on line A; F5; it stops there
#   2. WHILE STOPPED AT THAT BREAKPOINT: add a CONDITIONAL breakpoint on line B
#      and DELETE the breakpoint on line A
#   3. F5 -- reported broken: "continua hasta el final" (never stops at B)
#
# Same linked-exe shape as their ShumBlintDebug (compile --debug + link --exe --debug,
# attach by hand). The difference from run-conditional-bp-exe-smoke.ps1 is WHERE the
# conditional breakpoint is born: at a Breakpoint stop, not an async break -- and that the
# original breakpoint is removed in the same stop.
#
# Run from Windows PowerShell 5.1. ASCII only.

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$work    = Join-Path $PSScriptRoot "condbp-ws-work"
$compile = Join-Path $PSScriptRoot "..\..\src\Shumway.Compile\bin\x64\Release\net10.0\shumway-compile.exe"
$link    = Join-Path $PSScriptRoot "..\..\src\Shumway.Link\bin\x64\Release\net10.0\shumway-link.exe"

foreach ($f in @($devenv, $compile, $link)) { if (-not (Test-Path $f)) { throw "missing $f" } }

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
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

# Line A: "mid(N)," (the unconditional breakpoint). Line B: "Doubled is N * 2." (the
# conditional one). Found by content so the .pl above can be edited freely.
$lines = Get-Content $pl
$lineA = 0; $lineB = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'mid\(N\)')          { $lineA = $i + 1 }
    if ($lines[$i] -match 'Doubled is N \* 2') { $lineB = $i + 1 }
}
if ($lineA -eq 0 -or $lineB -eq 0) { throw "did not find the two breakpoint lines" }
$condition = "0 =:= N mod 500"
Write-Host "line A (unconditional): $lineA -> $($lines[$lineA - 1].Trim())"
Write-Host "line B (conditional):   $lineB -> $($lines[$lineB - 1].Trim())  [$condition]"

Write-Host "[0/6] compile --debug + link --exe --debug ..."
& $compile --debug $pl -o (Join-Path $work "wsapp.shmo")
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
& $link (Join-Path $work "wsapp.shmo") --goal main --exe (Join-Path $work "wsapp") --debug
if ($LASTEXITCODE -ne 0) { throw "link failed" }
$exe = Join-Path $work "wsapp.exe"
if (-not (Test-Path $exe)) { throw "no exe produced" }

$stderrLog = Join-Path $env:TEMP "shumway-condbp-ws-stderr.log"
$stdoutLog = Join-Path $env:TEMP "shumway-condbp-ws-stdout.log"
Remove-Item $stderrLog, $stdoutLog -ErrorAction SilentlyContinue

$componentLog = Join-Path $env:TEMP "shumway-debug\component.log"
$componentStart = 0
if (Test-Path $componentLog) { $componentStart = (Get-Item $componentLog).Length }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class RotFinderWs
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
    Write-Host "[1/6] starting wsapp.exe (stderr -> $stderrLog) ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $dbgProc = Start-Process -FilePath $exe -PassThru -NoNewWindow `
        -RedirectStandardError $stderrLog -RedirectStandardOutput $stdoutLog
    Write-Host "  pid $($dbgProc.Id)"

    Write-Host "[2/6] starting devenv /rootsuffix Exp ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    Remove-Item Env:\SHUMWAY_DEBUG_DIAG
    $dte = Invoke-WithRetry { $d = [RotFinderWs]::FindDte($vsProc.Id); if ($null -eq $d) { throw "no DTE yet" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    Write-Host "[3/6] attaching to wsapp.exe ..."
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

    Write-Host "[4/6] Break All twice (module bootstrap) ..."
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    Start-Sleep -Seconds 4
    Invoke-WithRetry { $dte.Debugger.Break($true) } 15 3000
    Start-Sleep -Seconds 3
    Select-PrologThread | Out-Null

    # The single-file exe serves a MATERIALIZED copy of the source; F9 must target that
    # file. ActiveDocument names it when the stop navigation opened it — but a freshly
    # reset window layout may leave no active document, so fall back to finding the
    # materialized copy itself (%TEMP%\shumway-debug\<exe-hash>\wsapp.pl, newest).
    $bpFile = $null
    try {
        $doc = $dte.ActiveDocument
        if ($doc -and $doc.FullName -match 'wsapp\.pl$') { $bpFile = $doc.FullName }
    } catch { }
    if (-not $bpFile) {
        $mat = Get-ChildItem (Join-Path $env:TEMP "shumway-debug\*\wsapp.pl") -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($mat) { $bpFile = $mat.FullName }
    }
    if (-not $bpFile) { $bpFile = $pl }
    Write-Host "  breakpoint file: $bpFile"

    # --- the user's flow, step 1: unconditional breakpoint on line A; F5; stop there ---
    Write-Host "[5/6] unconditional breakpoint at line $lineA; F5 ..."
    Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $bpFile, $lineA) | Out-Null } 10 2000
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    $stoppedAtA = Wait-ForBreak 30
    Write-Host "  stopped at A: $stoppedAtA"
    $results["W5-1 unconditional bp stops"] = $stoppedAtA
    if ($stoppedAtA) { Select-PrologThread | Out-Null }

    # --- step 2, WHILE STOPPED AT A: add the conditional bp on B, delete A ---
    Write-Host "  while stopped: adding conditional bp at line $lineB, deleting line-$lineA bp ..."
    $bpAdded = $true
    try {
        Invoke-WithRetry {
            $dte.Debugger.Breakpoints.Add("", $bpFile, $lineB, 1, $condition, 1) | Out-Null
        } 10 2000
    }
    catch { $bpAdded = $false; Write-Host "  Breakpoints.Add threw: $($_.Exception.Message)" }
    $results["W5-2 conditional add while stopped succeeds"] = $bpAdded

    $deleted = 0
    foreach ($bp in @($dte.Debugger.Breakpoints)) {
        if ($bp.FileLine -eq $lineA) { $bp.Delete(); $deleted++ }
    }
    Write-Host "  deleted $deleted breakpoint(s) at line $lineA"

    # --- step 3: F5 -- must stop at B where the condition holds, not run forever ---
    Write-Host "[6/6] F5 -> expecting a stop at line $lineB with N mod 500 == 0 ..."
    Invoke-WithRetry { $dte.Debugger.Go($false) } 15 2000
    $stoppedAtB = Wait-ForBreak 90
    Write-Host "  stopped: $stoppedAtB"

    $n1 = -1; $stopLine = 0
    if ($stoppedAtB) {
        Select-PrologThread | Out-Null
        $frames = Invoke-WithRetry {
            $f = @($dte.Debugger.CurrentThread.StackFrames) | ForEach-Object { $_.FunctionName }
            if (-not $f) { throw "no frames yet" }
            $f
        } 10 2000
        Write-Host "  frames: $($frames -join ' | ')"
        try { $stopLine = $dte.ActiveDocument.Selection.CurrentLine } catch { }
        $n1 = Get-TickN
        Write-Host "  N at the stop: $n1 (line $stopLine)"
    }
    $results["W5-3 stops at B where the condition holds"] =
        ($stoppedAtB -and $n1 -gt 0 -and ($n1 % 500) -eq 0)
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($dbgProc -and -not $dbgProc.HasExited) { $dbgProc.Kill() } } catch {}
}

Start-Sleep -Seconds 2

$stderr = @()
if (Test-Path $stderrLog) { $stderr = Get-Content $stderrLog }
$fails  = @($stderr | Where-Object { $_ -match "breakpoint condition .* fails \(run on\)" })
$holds  = @($stderr | Where-Object { $_ -match "breakpoint condition .* holds \(stop\)" })
$noCond = @($stderr | Where-Object { $_ -match "no condition attached" })
Write-Host ""
Write-Host "=== engine stderr (tail) ==="
$stderr | Select-Object -Last 8 | ForEach-Object { Write-Host "  $_" }
Write-Host "  [counts] fails=$($fails.Count) holds=$($holds.Count) no-condition=$($noCond.Count)"
$results["W5-4 the condition reached the engine"] =
    ($fails.Count -gt 0 -and $holds.Count -ge 1)

if (Test-Path $componentLog) {
    $stream = [System.IO.File]::Open($componentLog, 'Open', 'Read', 'ReadWrite')
    try {
        $stream.Position = $componentStart
        $reader = New-Object System.IO.StreamReader($stream)
        $newLog = $reader.ReadToEnd() -split "`r?`n"
    } finally { $stream.Dispose() }
    Write-Host ""
    Write-Host "=== component.log (this run): breakpoint traffic ==="
    $newLog | Where-Object { $_ -match "snapshot:|condition parsed|breakpoint enabled|breakpoint disabled" } |
        Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" }
}

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
