@echo off
rem Build NetProbe: Roslyn from the .NET SDK, target .NET Framework 4.8 (built into Windows 10/11)
setlocal
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
for /f "delims=" %%d in ('dir /b /ad /o-n "C:\Program Files\dotnet\sdk"') do if not defined SDK set "SDK=C:\Program Files\dotnet\sdk\%%d"
cd /d "%~dp0"
dotnet "%SDK%\Roslyn\bincore\csc.dll" -nologo -target:winexe -optimize+ -langversion:latest -nostdlib -platform:anycpu -win32icon:app.ico -win32manifest:app.manifest -out:..\NetProbe.exe -r:"%FW%\mscorlib.dll" -r:"%FW%\System.dll" -r:"%FW%\System.Core.dll" -r:"%FW%\System.Drawing.dll" -r:"%FW%\System.Windows.Forms.dll" -r:"%FW%\System.Web.Extensions.dll" -r:"%FW%\System.IO.Compression.dll" -r:"%FW%\System.IO.Compression.FileSystem.dll" *.cs
endlocal