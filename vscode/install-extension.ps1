# ADR-036 — builds shumway-dap, packages the Shumway VS Code debug extension as a real
# .vsix (a folder copied into ~/.vscode/extensions is IGNORED by modern VS Code — it
# tracks installs in its own extensions.json), and installs it with the `code` CLI.
#
#   powershell -ExecutionPolicy Bypass -File vscode\install-extension.ps1
#
# After it, RESTART VS Code and open a .pl file.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$ext  = Join-Path $PSScriptRoot 'shumway-debug'
$out  = Join-Path $PSScriptRoot 'shumway-debug-0.1.4.vsix'

Write-Host '[1/4] publishing shumway-dap (Release x64)...'
dotnet publish (Join-Path $repo 'src\Shumway.Dap') -c Release -p:Platform=x64 -v:q --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
$publish = Join-Path $repo 'src\Shumway.Dap\bin\x64\Release\net10.0\publish'
if (-not (Test-Path (Join-Path $publish 'shumway-dap.exe'))) { throw "no adapter at $publish" }

Write-Host '[2/4] staging the adapter into the extension...'
$bin = Join-Path $ext 'bin'
Remove-Item $bin -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Copy-Item (Join-Path $publish '*') $bin -Recurse

Write-Host '[3/4] packaging the .vsix...'
# A .vsix is a zip in OPC layout: the manifest + content types at the root, the
# extension's files under extension/.
$stage = Join-Path $env:TEMP 'shumway-vsix-stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'extension') | Out-Null
Copy-Item (Join-Path $ext 'extension.vsixmanifest') $stage
@'
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="json" ContentType="application/json"/>
  <Default Extension="vsixmanifest" ContentType="text/xml"/>
  <Default Extension="md" ContentType="text/markdown"/>
  <Default Extension="exe" ContentType="application/octet-stream"/>
  <Default Extension="dll" ContentType="application/octet-stream"/>
  <Default Extension="pdb" ContentType="application/octet-stream"/>
</Types>
'@ | Out-File -LiteralPath (Join-Path $stage '[Content_Types].xml') -Encoding utf8
Get-ChildItem $ext | Where-Object { $_.Name -ne 'extension.vsixmanifest' } |
    Copy-Item -Destination (Join-Path $stage 'extension') -Recurse
Remove-Item $out -Force -ErrorAction SilentlyContinue
# ZipFile, not Compress-Archive: the OPC-required [Content_Types].xml name is a
# PowerShell wildcard, and Compress-Archive chokes on it.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $out)
Remove-Item $stage -Recurse -Force
Write-Host "packaged: $out"

Write-Host '[4/4] installing with the code CLI...'
# Remove any earlier folder-copy install (it shadows nothing, but keep it clean).
Remove-Item (Join-Path $env:USERPROFILE '.vscode\extensions\shumway.shumway-debug-0.1.0') `
    -Recurse -Force -ErrorAction SilentlyContinue
$code = Get-Command code -ErrorAction SilentlyContinue
if ($code) {
    & code --install-extension $out --force
    if ($LASTEXITCODE -ne 0) { throw 'code --install-extension failed' }
    Write-Host 'installed. RESTART VS Code, open a .pl file, and press F5.'
} else {
    Write-Host 'the `code` CLI is not on PATH. In VS Code: Ctrl+Shift+P ->'
    Write-Host "  'Extensions: Install from VSIX...' and pick $out"
}
