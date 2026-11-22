@echo off
chcp 65001 >nul
title DesktopSnow builder

echo === DesktopSnow build started ===
echo Folder: %~dp0
echo %~dp0 | find /i ".zip" >nul && goto inzip

echo Step 1: closing old instances...
taskkill /f /im DesktopSnow.exe >nul 2>&1
taskkill /f /im Snow.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul

echo Step 2: looking for C# compiler...
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" goto nocsc
echo     found: %CSC%

echo Step 3: compiling Snow.cs ...
"%CSC%" /nologo /optimize+ /target:winexe /out:"%~dp0DesktopSnow.exe" ^
  /lib:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF" ^
  /lib:"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF" ^
  /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll ^
  /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:System.dll /r:System.Core.dll "%~dp0Snow.cs"
if errorlevel 1 goto buildfail

if not exist "%~dp0DesktopSnow.exe" goto nowrite

echo Step 4: build OK, launching...
start "" "%~dp0DesktopSnow.exe"
echo === Done. Snow should start. Close this window. ===
pause
exit /b 0

:nocsc
echo [ERROR] csc.exe not found. .NET Framework 4.x required.
pause
exit /b 1

:buildfail
echo [ERROR] Build failed. See the messages above.
echo If it mentioned a busy or locked file, close the running DesktopSnow:
echo   tray icon near the clock - Exit, or Task Manager - kill DesktopSnow.exe
pause
exit /b 1

:nowrite
echo [ERROR] exe was not created - something blocked writing (antivirus?).
pause
exit /b 1

:inzip
echo [ERROR] You are running build.bat directly from the ZIP preview window.
echo Extract the whole folder first: right click the ZIP - Extract all,
echo open the extracted folder and run build.bat there.
pause
exit /b 1
