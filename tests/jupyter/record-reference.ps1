# Records what the reference server answers to the corpus (ADR-067 D8).
#
# Each corpus\NAME.cells is one session. This script writes, beside it:
#   NAME.requests.jsonl   the JSON-RPC lines our server is sent
#   NAME.expected.jsonl   the lines the reference answered, paths scrubbed
#
# The reference is Hercutalk's server running on desktop Shumway through
# Logtalk. Neither tree is part of this repository: pass where they are.
#
#   pwsh tests/jupyter/record-reference.ps1 -Logtalk C:\logtalk3 -Hercutalk C:\hercutalk
#   ... -Only 003-queries,004-retry      # some sessions only
#   ... -Backend swi -Exe C:/swipl/bin/swipl.exe -OutDir C:/work/swi
#       the same server on another Prolog, to tell what the reference does from
#       what the engine under it does. Never recorded into the corpus.
#
# A .cells file is a list of sections:
#   ### call        the lines below are a cell's code, sent as a call request
#   ### reference   after a call: the code the reference gets in its place
#   ### raw         the line below is sent as it is
param(
    [Parameter(Mandatory)] [string]$Logtalk,
    [Parameter(Mandatory)] [string]$Hercutalk,
    [ValidateSet('shumway', 'swi', 'sicstus')] [string]$Backend = 'shumway',
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\dist\Release\shumway.exe'),
    [string]$OutDir = '',
    [string[]]$Only = @(),
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$Logtalk = (Resolve-Path $Logtalk).Path
$Hercutalk = (Resolve-Path $Hercutalk).Path
$Exe = (Resolve-Path $Exe).Path
$loader = (Join-Path $Hercutalk 'logtalk_kernel\logtalk_server\loader.lgt') -replace '\\', '/'
if (-not (Test-Path $loader)) { throw "no Hercutalk server at $loader" }
$corpus = Join-Path $PSScriptRoot 'corpus'
if ($Backend -ne 'shumway' -and $OutDir -eq '') { throw "-Backend $Backend needs -OutDir: the corpus holds the Shumway recording only" }
if ($OutDir -eq '') { $OutDir = $corpus } else { New-Item -ItemType Directory -Force $OutDir | Out-Null }
$goal = "set_logtalk_flag(report,off),logtalk_load('$loader'),'::'(jupyter_server,start);halt"

function Read-Cells([string]$path) {
    $items = [System.Collections.Generic.List[object]]::new()
    $kind = $null
    $body = [System.Collections.Generic.List[string]]::new()
    $flush = {
        if ($null -eq $kind) { return }
        $text = ($body -join "`n").TrimEnd("`n")
        switch ($kind) {
            'call'      { $items.Add([pscustomobject]@{ Kind = 'call'; Code = $text; Reference = $null }) }
            'raw'       { $items.Add([pscustomobject]@{ Kind = 'raw'; Line = $text }) }
            'reference' {
                if ($items.Count -eq 0 -or $items[$items.Count - 1].Kind -ne 'call') {
                    throw "${path}: '### reference' must follow a call"
                }
                $items[$items.Count - 1].Reference = $text
            }
        }
    }
    foreach ($line in [System.IO.File]::ReadAllLines($path, $utf8)) {
        if ($line -match '^### (call|raw|reference)\s*$') {
            & $flush
            $kind = $Matches[1]
            $body.Clear()
        }
        elseif ($null -eq $kind) {
            if ($line.Trim() -ne '') { throw "${path}: text before the first section" }
        }
        else { $body.Add($line) }
    }
    & $flush
    return $items
}

function Request-Lines($items, [bool]$forReference) {
    $count = 0
    foreach ($item in $items) {
        if ($item.Kind -eq 'raw') { $item.Line; continue }
        $count++
        $code = if ($forReference -and $null -ne $item.Reference) { $item.Reference } else { $item.Code }
        [ordered]@{ jsonrpc = '2.0'; id = $count; method = 'call'; params = [ordered]@{ code = $code } } |
            ConvertTo-Json -Compress -Depth 5
    }
}

function Invoke-Reference([string[]]$lines, [string]$workDir) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    $arguments = if ($Backend -eq 'swi') {
        @('-s', (Join-Path $Logtalk 'integration\logtalk_swi.pl'), '-q', '-g', $goal)
    } elseif ($Backend -eq 'sicstus') {
        @('-l', (Join-Path $Logtalk 'integration\logtalk_sicstus.pl'), '--noinfo', '--goal', "$goal.", '--nologo')
    } else {
        @((Join-Path $Logtalk 'integration\logtalk_shumway.pl'), '--quiet', '--goal', "$goal.")
    }
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $workDir
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.UseShellExecute = $false
    $start.Environment['LOGTALKHOME'] = $Logtalk
    $start.Environment['LOGTALKUSER'] = $Logtalk
    $process = [System.Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    foreach ($line in $lines) { $process.StandardInput.Write($line + "`n") }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw "the reference did not finish in $TimeoutSeconds s"
    }
    return [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result }
}

# A recorded answer must not carry this machine's directories.
function Hide-Paths([string]$text, [string]$workDir) {
    foreach ($pair in @(@($workDir, '$CWD'), @($Hercutalk, '$HERCUTALK'), @($Logtalk, '$LOGTALK'))) {
        $forward = $pair[0] -replace '\\', '/'
        $escaped = $pair[0] -replace '\\', '\\'
        foreach ($form in @($forward, $escaped, $pair[0])) {
            $text = [regex]::Replace($text, [regex]::Escape($form), $pair[1].Replace('$', '$$'), 'IgnoreCase')
        }
    }
    return $text
}

$failed = 0
foreach ($cells in Get-ChildItem $corpus -Filter *.cells | Sort-Object Name) {
    $name = $cells.BaseName
    if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }
    $items = Read-Cells $cells.FullName
    $ours = @(Request-Lines $items $false)
    $theirs = @(Request-Lines $items $true)

    $workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("jupyter-ref-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $workDir | Out-Null
    try {
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-Reference $theirs $workDir
        $answers = @($result.Out -split "`r?`n" | Where-Object { $_.StartsWith('{"jsonrpc"') } |
            ForEach-Object { Hide-Paths $_ $workDir })
    }
    finally { Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue }

    if ($Backend -eq 'shumway') {
        [System.IO.File]::WriteAllText((Join-Path $OutDir "$name.requests.jsonl"), (($ours -join "`n") + "`n"), $utf8)
    }
    [System.IO.File]::WriteAllText((Join-Path $OutDir "$name.expected.jsonl"), (($answers -join "`n") + "`n"), $utf8)
    $note = ''
    if ($answers.Count -ne $ours.Count) {
        $failed++
        $note = "  <-- $($ours.Count) requests"
    }
    '{0,-28} {1,3} answers in {2,5:F1} s{3}' -f $name, $answers.Count, $clock.Elapsed.TotalSeconds, $note
}
if ($failed -gt 0) {
    "$failed session(s) answered a different number of lines than they were sent: read them before keeping the recording."
}
