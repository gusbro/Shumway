# Runs one measurement hook of the published WebShumway page in a headless
# browser and prints the report it POSTs (docs/benchmarks/browser.md, "Running
# a hook headless"). It serves the publish with WebShumwayServe.ps1, opens the
# page on the hook with a throwaway profile, waits until the report matches
# -Done, and then kills the browser tree and the server, success or not.
#
#   powershell -File src/Shumway.Web/WebShumwayHook.ps1 `
#       -Hook '#wasmprobe=exits&n=100000' -Done 'probe exits: done|CRASHED'
#
# Publish first, with the flags the hooks need:
#   dotnet publish src/Shumway.Web -c Release -p:ShumwayWasmTier=true -p:ShumwayDiag=true
param(
  [Parameter(Mandatory)] [string]$Hook,      # the URL fragment, '#' included
  [Parameter(Mandatory)] [string]$Done,      # regex on the report that ends the run
  [string]$Out = 'hook-report.txt',          # where the report is collected
  [int]$Port = 9031,
  [int]$TimeoutSec = 900,
  [string]$Browser = '',                     # chrome.exe or msedge.exe; found if empty
  [string]$Root = '',                        # the site to serve; the last publish if empty
  [string]$JsFlags = ''                      # V8 flags for the run, e.g. '--no-liftoff'
)
$ErrorActionPreference = 'Stop'

# The server appends to this file from its own process: a relative path, or a
# POSIX-style one from a bash shell (/c/...), makes every POST fail with 500
# and the hook runs with nothing recorded.
if ($Out -match '^/([a-zA-Z])/(.*)$') { $Out = $Matches[1] + ':\' + ($Matches[2] -replace '/', '\') }
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
if (Test-Path $Out) { Remove-Item $Out -Force }

if (-not $Browser) {
  $Browser = @(
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $Browser) { throw 'No Chrome or Edge found; pass -Browser.' }
}

$shell = (Get-Process -Id $PID).Path
$serveArgs = @(
  '-NoProfile', '-ExecutionPolicy', 'Bypass',
  '-File', (Join-Path $PSScriptRoot 'WebShumwayServe.ps1'),
  '-Port', $Port, '-Collect', $Out)
if ($Root) { $serveArgs += @('-Root', $Root) }
$server = Start-Process $shell -PassThru -WindowStyle Hidden -ArgumentList $serveArgs
$profileDir = Join-Path ([System.IO.Path]::GetTempPath()) ('webshumway-hook-' + [guid]::NewGuid().ToString('N'))
$page = $null
$ok = $false
try {
  Start-Sleep -Seconds 3
  $browserArgs = @('--headless=new', "--user-data-dir=$profileDir", '--no-first-run',
    '--no-default-browser-check')
  if ($JsFlags) { $browserArgs += "--js-flags=$JsFlags" }
  $page = Start-Process $Browser -PassThru -ArgumentList ($browserArgs + "http://localhost:$Port/$Hook")
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if ((Test-Path $Out) -and ((Get-Content $Out -Raw) -match $Done)) { $ok = $true; break }
  }
} finally {
  # A headless browser left behind keeps its engine and its memory for as long
  # as the machine is up; so does the server.
  foreach ($p in @($page, $server)) {
    if ($p -and -not $p.HasExited) { & taskkill /T /F /PID $p.Id 2>&1 | Out-Null }
  }
  Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profileDir) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 1
  Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
}

if (Test-Path $Out) { Get-Content $Out }
if (-not $ok) {
  Write-Output "WebShumwayHook: no report matched /$Done/ within ${TimeoutSec}s"
  exit 1
}
