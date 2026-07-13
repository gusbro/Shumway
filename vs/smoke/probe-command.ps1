# Is the command REGISTERED at all? DTE knows commands by (guid, id); the canonical name is
# a convenience that only exists if the command table says so. This asks the primary question.

$ErrorActionPreference = 'Stop'
$devenv  = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
$program = (Resolve-Path (Join-Path $PSScriptRoot "launch.pl")).Path

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RotFinder3
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

function Retry([scriptblock]$a, [int]$n = 30, [int]$ms = 2000) {
    for ($i = 1; $i -le $n; $i++) {
        try { return & $a } catch { if ($i -eq $n) { throw }; Start-Sleep -Milliseconds $ms }
    }
}

$vs = Start-Process -FilePath $devenv -ArgumentList "/rootsuffix Exp" -PassThru
try {
    $dte = Retry { $d = [RotFinder3]::FindDte($vs.Id); if (-not $d) { throw "no dte" }; $d } 90 2000
    Retry { $null = $dte.Solution } 30 2000
    Start-Sleep -Seconds 10
    Retry {
        $ops = $dte.ItemOperations
        if (-not $ops) { throw "shell not ready" }
        $ops.OpenFile($program) | Out-Null
    } 30 2000
    Start-Sleep -Seconds 3

    Write-Host "--- commands in our command set ---"
    $set = "{c74e33bf-2316-41cf-a971-b3bc83745619}"
    $found = 0
    foreach ($c in @($dte.Commands)) {
        if ($c.Guid -eq $set) {
            $found++
            Write-Host ("  id={0} name='{1}' enabled={2}" -f $c.ID, $c.Name, $c.IsAvailable)
        }
    }
    if ($found -eq 0) { Write-Host "  NONE -- the command table did not register" }

    Write-Host "--- Debug menu items ---"
    try {
        $bar = $dte.CommandBars
        $debugMenu = $bar.Item("Debug")
        foreach ($ctl in @($debugMenu.Controls)) { Write-Host "  $($ctl.Caption)" }
    } catch { Write-Host "  CommandBars threw: $($_.Exception.Message)" }
}
finally {
    try { if (-not $vs.HasExited) { $vs.Kill() } } catch {}
}
