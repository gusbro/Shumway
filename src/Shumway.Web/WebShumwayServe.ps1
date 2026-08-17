# Serves the published WebShumway site locally, with the cross-origin-isolation
# headers the threads build needs (COOP/COEP) sent for real — so there is no
# service-worker synthesis and no first-visit reload.
#
#   dotnet publish src/Shumway.Web -c Release
#   powershell -File src/Shumway.Web/WebShumwayServe.ps1            # port 8080
#   powershell -File src/Shumway.Web/WebShumwayServe.ps1 -Port 9000
#   powershell -File src/Shumway.Web/WebShumwayServe.ps1 -Root <otro wwwroot>
param(
  [string]$Root = (Join-Path $PSScriptRoot 'bin\Release\net10.0\publish\wwwroot'),
  [int]$Port = 8080
)

if (-not (Test-Path $Root -PathType Container)) {
  Write-Error "No site at $Root - run: dotnet publish src/Shumway.Web -c Release"
  exit 1
}

$prefix = "http://localhost:$Port/"

$mime = @{
  '.html'        = 'text/html; charset=utf-8'
  '.js'          = 'text/javascript; charset=utf-8'
  '.mjs'         = 'text/javascript; charset=utf-8'   # the threads worker: served as
                                                      # octet-stream it dies opaquely
  '.css'         = 'text/css; charset=utf-8'
  '.wasm'        = 'application/wasm'
  '.json'        = 'application/json; charset=utf-8'
  '.svg'         = 'image/svg+xml'
  '.ico'         = 'image/x-icon'
  '.png'         = 'image/png'
  '.pl'          = 'text/plain; charset=utf-8'
  '.dat'         = 'application/octet-stream'
  '.blat'        = 'application/octet-stream'
  '.shum'        = 'application/octet-stream'
  '.woff2'       = 'font/woff2'
  '.webmanifest' = 'application/manifest+json'
}

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($prefix)
$listener.Start()
Write-Host "WebShumway at $prefix  (serving $Root, Ctrl+C to stop)"

while ($listener.IsListening) {
  $ctx = $listener.GetContext()
  $res = $ctx.Response
  try {
    $urlPath = [uri]::UnescapeDataString($ctx.Request.Url.LocalPath)
    if ($urlPath -eq '/') { $urlPath = '/index.html' }
    $filePath = Join-Path $Root ($urlPath.TrimStart('/').Replace('/', '\'))

    if (Test-Path $filePath -PathType Leaf) {
      $ext = [System.IO.Path]::GetExtension($filePath).ToLower()
      $res.ContentType = if ($mime[$ext]) { $mime[$ext] } else { 'application/octet-stream' }
      # The isolation headers: SharedArrayBuffer (threads) requires them.
      $res.Headers.Add('Cross-Origin-Opener-Policy', 'same-origin')
      $res.Headers.Add('Cross-Origin-Embedder-Policy', 'credentialless')
      # Always fresh: this server exists to look at the build just made.
      $res.Headers.Add('Cache-Control', 'no-store')
      $bytes = [System.IO.File]::ReadAllBytes($filePath)
      $res.ContentLength64 = $bytes.Length
      $res.OutputStream.Write($bytes, 0, $bytes.Length)
    } else {
      $res.StatusCode = 404
      $body = [System.Text.Encoding]::UTF8.GetBytes("404 Not Found: $urlPath")
      $res.ContentLength64 = $body.Length
      $res.OutputStream.Write($body, 0, $body.Length)
    }
  } catch {
    # One broken request (client gone mid-write, unreadable file) must not
    # take the server down.
    try { $res.StatusCode = 500 } catch {}
  } finally {
    try { $res.OutputStream.Close() } catch {}
  }
}
