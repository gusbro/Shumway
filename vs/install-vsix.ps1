# Reinstall the Shumway debugger extension.
#
# WHY A SCRIPT. VSIXInstaller compares VERSIONS, not contents: install a rebuilt VSIX whose
# version did not change and it says "This extension is already installed to all applicable
# products" and leaves the OLD one in place. You then debug with an extension that does not
# match the engine -- and the pair talk over a shared memory buffer whose layout they must
# AGREE on. When they do not, there is no Prolog stack: just the engine's own C#.
#
# So: uninstall by identity, then install. Always.
#
#   powershell -ExecutionPolicy Bypass -File vs\install-vsix.ps1            (release VSIX)
#   powershell -ExecutionPolicy Bypass -File vs\install-vsix.ps1 -Exp       (the Exp hive, for smokes)
#
# Run from Windows PowerShell 5.1. ASCII only. Close Visual Studio first.

param(
    [switch]$Exp,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'

$id = "Shumway.Debugger.50e1d3f2-52aa-4991-855f-f6426e9ae257"
$installer = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe"
$vsix = Join-Path $PSScriptRoot "Shumway.Debugger.Vsix\bin\$Configuration\Shumway.Debugger.vsix"

if (-not (Test-Path $installer)) { throw "VSIXInstaller not found: $installer" }
if (-not (Test-Path $vsix)) {
    throw "no VSIX at $vsix -- build it first with desktop MSBuild:`n" `
        + "  & 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' vs\Shumway.Debugger.sln /p:Configuration=$Configuration"
}

if (@(Get-Process -Name devenv -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Visual Studio is running. Close it -- the installer cannot replace a loaded extension."
}

$rootSuffix = if ($Exp) { @("/rootSuffix:Exp") } else { @() }

Write-Host "uninstalling $id ..."
$args = @("/uninstall:$id", "/quiet") + $rootSuffix
$p = Start-Process -FilePath $installer -ArgumentList $args -Wait -PassThru
# 1002 = not installed. Anything else non-zero is a real failure.
if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 1002) {
    Write-Host "  (uninstall exit $($p.ExitCode) -- see %TEMP%\dd_VSIXInstaller_*.log)"
}

Write-Host "installing $vsix ..."
$args = @($vsix, "/quiet") + $rootSuffix
$p = Start-Process -FilePath $installer -ArgumentList $args -Wait -PassThru
if ($p.ExitCode -ne 0) {
    throw "install failed (exit $($p.ExitCode)) -- see the newest %TEMP%\dd_VSIXInstaller_*.log"
}

$manifest = Join-Path $PSScriptRoot "Shumway.Debugger.Vsix\source.extension.vsixmanifest"
$version = ([xml](Get-Content $manifest)).PackageManifest.Metadata.Identity.Version
Write-Host ""
Write-Host "installed version $version. Start Visual Studio and attach."
