@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%~dp0assets\orbit.ico" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\make-icon.ps1"
"%CSC%" -nologo -target:winexe -out:"%~dp0CodexOrbit.exe" -win32icon:"%~dp0assets\orbit.ico" -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Management.dll -r:System.Web.Extensions.dll "%~dp0src\CodexOrbitApp.cs"
if errorlevel 1 (echo BUILD FAILED & pause & exit /b 1)
echo OK: CodexOrbit.exe
