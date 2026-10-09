# The test gate, by level.
#
#   powershell -File tests/gate.ps1                 # level from the changed files
#   powershell -File tests/gate.ps1 -Level step     # Core, Interpreter, Compiler, ISO, Embedding
#   powershell -File tests/gate.ps1 -Level engine   # + the wasm tests and the net48 build
#   powershell -File tests/gate.ps1 -Level full     # + the web build: before a push or a merge
#
# Without -Level the gate compares the working tree and -Base (default HEAD)
# and picks `engine` when a change touches the engine's core, the interpreter
# or the wasm tier, `step` otherwise. A push or a merge to main runs `full`.
param(
    [ValidateSet('', 'step', 'engine', 'full')] [string] $Level = '',
    [string] $Base = 'HEAD'
)

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ($Level -eq '') {
    $changed = @(git diff --name-only $Base) + @(git ls-files --others --exclude-standard)
    $engine = $changed | Where-Object {
        $_ -like 'src/Shumway.Core/*' -or $_ -like 'src/Shumway.Interpreter/*' -or
        $_ -like 'src/Shumway.Compiler.Wasm/*' -or $_ -like 'src/Shumway.Web/*' -or
        $_ -like 'src/Shared/*' -or $_ -like '*.props' -or $_ -like '*.csproj'
    }
    $Level = if ($engine) { 'engine' } else { 'step' }
}

$failed = $false
$sw = [System.Diagnostics.Stopwatch]::StartNew()
function Step([string] $name, [scriptblock] $body) {
    $t = [System.Diagnostics.Stopwatch]::StartNew()
    $out = & $body 2>&1
    $lines = $out | Select-String -Pattern 'error|Warning\(s\)|Error\(s\)|Passed!|Failed!|\[parallel\]|\[wasm\]|RESULT' |
        ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0 -or ($lines -match 'Failed!|RESULT: FAILED|[1-9]\d* Error\(s\)')) { $script:failed = $true }
    "=== {0,-22} {1,5:N0} s" -f $name, $t.Elapsed.TotalSeconds
    $lines | ForEach-Object { "    $_" }
}

"gate level: $Level"
Step 'build' { dotnet build --nologo -v q }
Step 'core' { dotnet test tests/Shumway.Tests.Core/ --nologo -v q --no-build }
Step 'interpreter' { dotnet test tests/Shumway.Tests.Interpreter/ --nologo -v q --no-build }
Step 'compiler' { dotnet test tests/Shumway.Tests.Compiler/ --nologo -v q --no-build }
Step 'iso' { dotnet test tests/Shumway.Tests.IsoConformance/ --nologo -v q --no-build }
Step 'embedding (parallel)' { powershell -NoProfile -File tests/test-embedding-parallel.ps1 }
if ($Level -in 'engine', 'full') {
    Step 'wasm (parallel)' { powershell -NoProfile -File tests/test-wasm-parallel.ps1 }
    Step 'net48 build' { dotnet build -p:ShumwayNetFx=true --nologo -v q }
}
if ($Level -eq 'full') {
    Step 'web build' { dotnet build src/Shumway.Web/ --nologo -v q }
}
"gate {0} in {1:N0} s: {2}" -f $Level, $sw.Elapsed.TotalSeconds, $(if ($failed) { 'FAILED' } else { 'PASSED' })
if ($failed) { exit 1 }
