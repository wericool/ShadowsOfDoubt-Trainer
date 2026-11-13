@echo off
setlocal
rem Needs only a C# compiler: VS Build Tools (Roslyn) or .NET Framework 4.x. No .NET SDK required.
set CSC=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" -nologo -unsafe -optimize+ -platform:x64 -target:winexe -out:"%~dp0ShadowsOfDoubtTrainer.exe" -r:System.Windows.Forms.dll -r:System.Drawing.dll -win32manifest:"%~dp0app.manifest" "%~dp0src\*.cs"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo OK: %~dp0ShadowsOfDoubtTrainer.exe
