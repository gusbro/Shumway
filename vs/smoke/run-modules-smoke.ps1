# ADR-035 -- WHEN DOES A PROLOG FRAME BECOME A PROLOG FRAME?
#
# The user's report: attach to a running engine, Break All, and the Prolog frames are there
# but GREY -- no language, no source, not clickable. Open the .pl in the editor by hand, break
# again, and now they work. Which is backwards: the engine knows exactly which files it
# consulted, so the debugger should find them the way it finds a C# source file, not wait to
# be shown one.
#
#   M1  the FIRST Break All after attaching gives frames that say Prolog
#   M2  ...and that carry a source file
#   M3  a second break still does (nothing regressed)
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
public static class RotFinder8
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

# What the Call Stack window would show for the Prolog frames of the running thread.
function Get-PrologFrames($dte, [string]$pattern) {
    Invoke-WithRetry {
        foreach ($t in @($dte.Debugger.CurrentProgram.Threads)) {
            $frames = @($t.StackFrames)
            $mine = @($frames | Where-Object { $_.FunctionName -match $pattern })
            if ($mine.Count -gt 0) {
                $dte.Debugger.CurrentThread = $t
                return @($mine | ForEach-Object {
                    [pscustomobject]@{ Name = $_.FunctionName; Language = $_.Language }
                })
            }
        }
        throw "no Prolog frames yet"
    } 5 2000
}

function Break-And-Report([string]$what, $dte, [string]$pattern = '(^|:)(spin|work|go)([(/!]|$)') {
    Invoke-WithRetry { $dte.Debugger.Break($false) } 3 2000
    for ($i = 0; $i -lt 25; $i++) {
        Assert-Time "waiting for the break ($what)"
        Start-Sleep -Seconds 1
        if ((Invoke-WithRetry { $dte.Debugger.CurrentMode } 5 1000) -eq 2) { break }
    }
    $f = @()
    try { $f = Get-PrologFrames $dte $pattern } catch { Write-Host "  no prolog frames: $($_.Exception.Message)" }
    Write-Host "=== $what ==="
    $f | Select-Object -First 4 | ForEach-Object { Write-Host ("   {0,-14} language='{1}'" -f $_.Name, $_.Language) }
    return $f
}

try {
    Write-Host "[1/4] shumway --debug pause-spin.pl, sitting at its prompt ..."
    $env:SHUMWAY_DEBUG_DIAG = "1"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $repl
    $psi.Arguments = "--debug `"$program`""
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $engine = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 2

    Write-Host "[2/4] devenv, and attach ..."
    $vsProc = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
    $dte = Invoke-WithRetry { $d = [RotFinder8]::FindDte($vsProc.Id); if (-not $d) { throw "no DTE" }; $d } 90 2000
    Invoke-WithRetry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10
    $target = Invoke-WithRetry {
        if ($engine.HasExited) { throw "the engine exited" }
        $p = @($dte.Debugger.LocalProcesses) | Where-Object { $_.ProcessID -eq $engine.Id }
        if (-not $p) { throw "engine pid $($engine.Id) not in LocalProcesses yet" }
        $p
    } 45 2000
    Invoke-WithRetry { $target.Attach() } 15 3000
    Start-Sleep -Seconds 8

    Write-Host "[3/4] consult a file from the TOP LEVEL, and run it ..."
    $later = ((Resolve-Path (Join-Path $PSScriptRoot "pause-later.pl")).Path) -replace '\\', '/'
    $engine.StandardInput.WriteLine("consult('$later').")
    $engine.StandardInput.Flush()
    Start-Sleep -Seconds 3
    $engine.StandardInput.WriteLine("later_go.")
    $engine.StandardInput.Flush()
    Start-Sleep -Seconds 4

    # THE FIRST BREAK OF THE SESSION, in a file nobody named on the command line and nobody
    # opened in the editor. The debugger knows it because the engine SAID SO when it consulted
    # it. Before that it learned the file names only from a stop that had already happened, so
    # the first break gave grey frames -- no language, no source, nothing to click -- and the
    # user had to open the .pl by hand and break again.
    $first = Break-And-Report "first break" $dte "(^|:)later_"
    $results["M1 the first break in a top-level consult says Prolog"] =
        ($first.Count -gt 0 -and @($first | Where-Object { $_.Language -ne "Prolog" }).Count -eq 0)

    Write-Host "[4/5] and again ..."
    Invoke-WithRetry { $dte.Debugger.Go($false) } 5 2000
    Start-Sleep -Seconds 3
    $second = Break-And-Report "second break" $dte "(^|:)later_"
    $results["M2 and so does the next one"] =
        ($second.Count -gt 0 -and @($second | Where-Object { $_.Language -ne "Prolog" }).Count -eq 0)

    # M3 -- THE FILE NAMED RELATIVELY, from the directory the ENGINE was run in.
    # `shumway --debug Blint.pl` in c:\temp consults c:\temp\Blint.pl -- and if the frames say
    # only "Blint.pl", the debugger resolves that against ITS OWN directory (Visual Studio's),
    # finds nothing, matches no module, and shows the stack grey. The engine is the only process
    # that knows where it was standing, so it is the one that has to say.
    Write-Host "[5/5] a second engine, started as `"--debug pause-spin.pl`" from the smoke dir ..."
    Invoke-WithRetry { $dte.Debugger.DetachAll() } 5 2000
    Start-Sleep -Seconds 2
    try { if (-not $engine.HasExited) { $engine.Kill() } } catch {}

    $psi2 = New-Object System.Diagnostics.ProcessStartInfo
    $psi2.FileName = $repl
    $psi2.Arguments = "--debug pause-spin.pl"          # RELATIVE
    $psi2.WorkingDirectory = $PSScriptRoot             # ...to here
    $psi2.RedirectStandardInput = $true
    $psi2.RedirectStandardOutput = $true
    $psi2.UseShellExecute = $false
    $engine = [System.Diagnostics.Process]::Start($psi2)
    Start-Sleep -Seconds 3

    $target2 = Invoke-WithRetry {
        if ($engine.HasExited) { throw "the engine exited" }
        $p = @($dte.Debugger.LocalProcesses) | Where-Object { $_.ProcessID -eq $engine.Id }
        if (-not $p) { throw "engine pid $($engine.Id) not in LocalProcesses yet" }
        $p
    } 30 2000
    Invoke-WithRetry { $target2.Attach() } 15 3000
    Start-Sleep -Seconds 6
    $engine.StandardInput.WriteLine("go.")
    $engine.StandardInput.Flush()
    Start-Sleep -Seconds 5

    $third = Break-And-Report "break in a relatively-named file" $dte '(^|:)(spin|work|go)([(/!]|$)'
    $results["M3 a file named relatively says Prolog too"] =
        ($third.Count -gt 0 -and @($third | Where-Object { $_.Language -ne "Prolog" }).Count -eq 0)

    if (Test-Path $componentLog) {
        Write-Host ""
        Write-Host "--- component log ---"
        Get-Content $componentLog | Where-Object { $_ -notmatch "module load:" } |
            Select-Object -Last 14 | ForEach-Object { Write-Host "   $_" }
    }

    Write-Host ""
    Write-Host "--- results ---"
    $allOk = $true
    foreach ($key in $results.Keys) {
        $ok = $results[$key]
        if (-not $ok) { $allOk = $false }
        Write-Host ("{0,-40} : {1}" -f $key, $(if ($ok) { "PASS" } else { "FAIL" }))
    }
    Write-Host ""
    if ($allOk) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL" }
}
finally {
    try { if ($dte) { $dte.Debugger.Stop($false) } } catch {}
    try { if ($vsProc -and -not $vsProc.HasExited) { $vsProc.Kill() } } catch {}
    try { if ($engine -and -not $engine.HasExited) { $engine.Kill() } } catch {}
}
