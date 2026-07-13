# Builds the D4 E2E corpus: the native C DLL and the C# foreign assembly.
# ASCII only (PS 5.1 reads .ps1 as CP1252).

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# --- native.dll, with MSVC. vcvars is needed for cl to find its own headers and linker.
$vcvars = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "no vcvars64.bat at $vcvars" }

Push-Location $here
try {
    $cmd = "`"$vcvars`" >nul && cl /nologo /LD /Zi native.c /Fe:native.dll"
    cmd /c $cmd
    if (-not (Test-Path (Join-Path $here "native.dll"))) { throw "cl produced no native.dll" }
    Write-Host "built native.dll"
}
finally { Pop-Location }

# --- ForeignLib.dll (and its Shumway dependencies) into ./bin
dotnet build (Join-Path $here "ForeignLib\ForeignLib.csproj") -c Debug -v q --nologo
$out = Join-Path $here "ForeignLib\bin\Debug\net10.0"
if (-not (Test-Path (Join-Path $out "ForeignLib.dll"))) { throw "no ForeignLib.dll in $out" }
Write-Host "built ForeignLib.dll"

# The native DLL has to sit where the P/Invoke will look for it: next to the foreign
# assembly that declares it, and next to the REPL that loads them both.
Copy-Item (Join-Path $here "native.dll") $out -Force
$repl = Join-Path $here "..\..\..\src\Shumway.Repl\bin\Debug\net10.0"
if (Test-Path $repl) { Copy-Item (Join-Path $here "native.dll") $repl -Force }
Write-Host "native.dll copied next to the foreign assembly and the REPL"
