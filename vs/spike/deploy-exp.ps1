# Build + deploy the Shumway debugger VSIX into the VS Experimental hive.
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads .ps1 files as
# CP1252, so a UTF-8 em-dash decodes to a right-double-quote and silently
# terminates a string literal mid-line.
#
# HARD-WON DEV-LOOP LESSON (D0) - the one that cost a whole debugging session:
#
#   The VSIX project's MSBuild targets ALREADY deploy the extension to the Exp
#   hive on every build (DeployExtension, the standard VSIX F5 loop). Running
#   VSIXInstaller.exe on top of that installs a SECOND copy under a random
#   directory name, and then VS finds two copies of the same extension id and
#   version and silently drops BOTH:
#
#       "An extension with the same identifier and version was already
#        discovered at path ... The conflict cannot be resolved ... we are not
#        adding either copy to the cache"     (dd_VSIXInstaller_*.log)
#
#   VS itself reports NOTHING - no error, no activity-log entry, the Concord
#   components simply never load, and every leg of the spike fails as if the
#   code were broken. So: build deploys; never mix in VSIXInstaller; and always
#   assert exactly one installed copy (this script fails loudly otherwise).

param(
    [switch]$Clean   # purge every installed copy before building
)

$ErrorActionPreference = 'Stop'

$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$sln = Join-Path $PSScriptRoot "..\Shumway.Debugger.sln"

$expRoots = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -Filter "18.0*Exp" |
    ForEach-Object { Join-Path $_.FullName "Extensions" }

function Get-InstalledDirs {
    $dirs = @()
    foreach ($root in $expRoots) {
        if (-not (Test-Path $root)) { continue }
        $dirs += Get-ChildItem $root -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName "Shumway.Debugger.Concord.dll") } |
            ForEach-Object { $_.FullName }
    }
    return $dirs
}

if ($Clean) {
    Write-Host "[0/2] purging installed copies..."
    foreach ($d in @(Get-InstalledDirs)) { Write-Host "  removing $d"; Remove-Item $d -Recurse -Force }
}

Write-Host "[1/2] building (the VSIX project deploys to the Exp hive)..."
& $msbuild $sln /restore /p:Configuration=Debug /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "[2/2] verifying a single installed copy..."
$installed = @(Get-InstalledDirs)
if ($installed.Count -ne 1) {
    foreach ($d in $installed) { Write-Host "  copy: $d" }
    throw "$($installed.Count) installed copies - VS drops ALL of them on an id conflict. Re-run with -Clean."
}

$dll = Get-Item (Join-Path $installed[0] "Shumway.Debugger.Concord.dll")
Write-Host "deployed (single copy): $($dll.FullName)"
Write-Host "  $($dll.Length) bytes, $($dll.LastWriteTime)"
