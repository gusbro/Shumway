# ADR-035 D4 E2E -- an arity-compat module with interop, debugged end to end.
#
# The other two smokes prove the debugger works on plain Prolog. This one asks the question
# the whole project exists for -- can you debug a program that crosses languages?
#
#   E1  a breakpoint binds in a MODULE-LOCAL predicate (mangled internally to module$name)
#   E2  the call stack shows the names the user WROTE, not the mangled ones
#   E3  its variables are there
#   E4  a breakpoint in the C# foreign predicate hits, and the stack is MIXED: the C# frames
#       on top, the Prolog frames that called them underneath
#   E5  the program runs to completion through Prolog -> C# -> native C (result(16))
#
# Native FRAMES need "Enable native code debugging"; the native CALL is exercised either way
# (the answer, 16, can only come from the C function).
#
# Run from Windows PowerShell 5.1. ASCII only (PS 5.1 reads .ps1 as CP1252).

$ErrorActionPreference = 'Stop'

$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$repl    = Join-Path $PSScriptRoot "..\..\src\Shumway.Repl\bin\Debug\net10.0\shumway.exe"
$program = Join-Path $PSScriptRoot "interop\interop.pl"
$foreign = Join-Path $PSScriptRoot "interop\ForeignLib\bin\Debug\net10.0\ForeignLib.dll"
$csharp  = Join-Path $PSScriptRoot "interop\ForeignLib\Scaling.cs"
$trace   = Join-Path $PSScriptRoot "interop\interop-trace.txt"

foreach ($f in @($repl, $program, $foreign, $csharp)) {
    if (-not (Test-Path $f)) { throw "missing $f  (run interop\build-interop.ps1 first)" }
}
$repl    = (Resolve-Path $repl).Path
$program = (Resolve-Path $program).Path
$foreign = (Resolve-Path $foreign).Path
$csharp  = (Resolve-Path $csharp).Path
Remove-Item $trace -ErrorAction SilentlyContinue

# The breakpoint in Prolog: the body of step/2, a LOCAL predicate of the module.
$lines = Get-Content $program
$plLine = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*scale\(N, Scaled\)') { $plLine = $i + 1; break }
}
if ($plLine -eq 0) { throw "could not find the body of step/2 in interop.pl" }

# The breakpoint in C#: the first line of the foreign predicate's body.
$csLines = Get-Content $csharp
$csLine = 0
for ($i = 0; $i -lt $csLines.Count; $i++) {
    if ($csLines[$i] -match '^\s*int factor = Factor\(value\);') { $csLine = $i + 1; break }
}
if ($csLine -eq 0) { throw "could not find the body of Scale in Scaling.cs" }

Write-Host "prolog breakpoint: interop.pl:$plLine   ($($lines[$plLine-1].Trim()))"
Write-Host "c# breakpoint    : Scaling.cs:$csLine   ($($csLines[$csLine-1].Trim()))"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RotFinder4
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
$results = [ordered]@{}

try {
    Write-Host "[1/6] starting devenv /rootsuffix Exp ..."
    # A program with interop cannot be launched without naming its DLLs. A user types them into
    # Tools > Options > Shumway > Prolog Debugger; a script hands them over the same way it
    # hands over the engine path -- through the environment devenv inherits.
    $env:SHUMWAY_EXE = $repl
    $env:SHUMWAY_ARGS = "--foreign-dll `"$foreign`""
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    $dte = Invoke-WithRetry { $d = [RotFinder4]::FindDte($vsProc.Id); if (-not $d) { throw "no DTE yet" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10

    Write-Host "[2/6] opening interop.pl and setting the Prolog breakpoint ..."
    Invoke-WithRetry {
        $ops = $dte.ItemOperations
        if ($null -eq $ops) { throw "the shell is not ready" }
        $ops.OpenFile($program) | Out-Null
    } 30 2000
    Start-Sleep -Seconds 3

    Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $program, $plLine) | Out-Null } 10 2000

    $commandSet = "{c74e33bf-2316-41cf-a971-b3bc83745619}"
    $command = $dte.Commands.Item($commandSet, 0x0100)

    Write-Host "[3/6] launching ..."
    Invoke-WithRetry { $dte.ExecuteCommand($command.Name) } 5 2000

    function Wait-ForBreak([int]$Seconds = 40) {
        for ($i = 0; $i -lt $Seconds; $i++) {
            $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
            if ($m -eq 2) { return $true }
            Start-Sleep -Seconds 1
        }
        return $false
    }
    function Select-PrologThread {
        Invoke-WithRetry {
            $chosen = $null
            foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
                $names = @($t.StackFrames) | ForEach-Object { $_.FunctionName }
                if (@($names | Where-Object { $_ -match '^\w+/\d+$' -or $_ -match '!\d+$' -or $_ -match '^\[Shumway' -or $_ -match 'Scaling' }).Count -gt 0) {
                    $chosen = $t; break
                }
            }
            if (-not $chosen) { throw "no thread with Prolog on it yet" }
            $dte.Debugger.CurrentThread = $chosen
            $chosen
        } 10 2000
        Start-Sleep -Seconds 2
    }

    $stoppedInProlog = Wait-ForBreak 40
    $frames = @()
    $localsText = "(none)"
    if ($stoppedInProlog) {
        Select-PrologThread | Out-Null
        $frames = @($dte.Debugger.CurrentThread.StackFrames) | ForEach-Object { $_.FunctionName }
        Write-Host ""
        Write-Host "=== call stack, in the module-local step/2 ==="
        $frames | ForEach-Object { Write-Host "  $_" }
        Write-Host "=============================================="
        try {
            $f = @($dte.Debugger.CurrentThread.StackFrames) |
                Where-Object { $_.FunctionName -match '(^|:)step[(/!]' } | Select-Object -First 1
            if ($f) {
                $dte.Debugger.CurrentStackFrame = $f
                Start-Sleep -Seconds 2
                $locals = @($dte.Debugger.CurrentStackFrame.Locals) | ForEach-Object { "$($_.Name) = $($_.Value)" }
                if ($locals.Count -gt 0) { $localsText = ($locals -join ", ") }
            }
        } catch { $localsText = "threw: $($_.Exception.Message)" }
    }
    Write-Host "  locals of step/2: $localsText"

    $results["E1 a breakpoint binds in a module-local predicate"] = $stoppedInProlog
    # The names the user wrote. A mangled frame would read "interop`$step/2".
    $results["E2 the stack shows the user's names, not mangled"] =
        (@($frames | Where-Object { $_ -match '(^|:)step([(/!]|$)' }).Count -ge 1 -and
         @($frames | Where-Object { $_ -match '(^|:)run([(/!]|$)' }).Count -ge 1 -and
         @($frames | Where-Object { $_ -match '\$' }).Count -eq 0)
    $results["E3 its variables are there"] = ($localsText -match 'N = ')

    Write-Host "[4/6] breakpoint in the C# foreign predicate, then continue ..."
    $mixed = @()
    $stoppedInCSharp = $false
    if ($stoppedInProlog) {
        try {
            foreach ($bp in @($dte.Debugger.Breakpoints)) { $bp.Delete() }
            Invoke-WithRetry { $dte.Debugger.Breakpoints.Add("", $csharp, $csLine) | Out-Null } 10 2000
            Invoke-WithRetry { $dte.Debugger.Go($false) } 10 2000
            $stoppedInCSharp = Wait-ForBreak 40
            if ($stoppedInCSharp) {
                Select-PrologThread | Out-Null
                $mixed = @($dte.Debugger.CurrentThread.StackFrames) | ForEach-Object { $_.FunctionName }
                Write-Host ""
                Write-Host "=== call stack, inside the C# foreign predicate ==="
                $mixed | ForEach-Object { Write-Host "  $_" }
                Write-Host "=================================================="
            }
        } catch { Write-Host "  c# breakpoint threw: $($_.Exception.Message)" }
    }
    # Mixed = C# frames AND Prolog frames, in one stack, at one stop.
    $hasCSharp = @($mixed | Where-Object { $_ -match 'Scale' }).Count -ge 1
    $hasProlog = @($mixed | Where-Object { $_ -match '(^|:)(step|run|main)([(/!]|$)' }).Count -ge 1
    $results["E4 the stack is mixed: C# over Prolog"] = ($hasCSharp -and $hasProlog)

    Write-Host "[5/5] continue to the end ..."
    $finished = $false
    try {
        foreach ($bp in @($dte.Debugger.Breakpoints)) { $bp.Delete() }
        Invoke-WithRetry { $dte.Debugger.Go($false) } 10 2000
        for ($i = 0; $i -lt 40; $i++) {
            $m = Invoke-WithRetry { $dte.Debugger.CurrentMode } 10 1000
            if ($m -eq 1) { $finished = $true; break }
            Start-Sleep -Seconds 1
        }
    } catch { Write-Host "  Go threw: $($_.Exception.Message)" }

    $answer = (Get-Content $trace -ErrorAction SilentlyContinue) -join ""
    Write-Host "  the program's answer: '$answer'  (exited: $finished)"
    # 4 -> C# -> C (x2) -> 8 -> C# -> C (x2) -> 16. Only the native function can produce it.
    $results["E5 Prolog -> C# -> native ran to completion"] = ($answer -match 'result\(16\)')

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
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    Get-Process -Name shumway -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
