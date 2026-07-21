dotnet build C:\claude\Shumway\Shumway.slnx -c Release
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" C:\claude\Shumway\vs\Shumway.Debugger.sln /p:Configuration=Release 
powershell -ExecutionPolicy Bypass -File C:\claude\Shumway\vs\install-vsix.ps1 -Configuration Release 
