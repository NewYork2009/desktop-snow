@echo off
chcp 65001 >nul
title DesktopSnow builder

echo === DesktopSnow build started ===
echo Step 1: closing old instance...
taskkill /f /im DesktopSnow.exe >nul 2>&1
taskkill /f /im Snow.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul

echo Step 2: looking for C# compiler...
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. .NET Framework 4.x required.
    pause
    exit /b 1
)
echo     found: %CSC%

echo Step 3: compiling Snow.cs ...
"%CSC%" /nologo /optimize+ /target:winexe /out:"%~dp0DesktopSnow.exe" ^
  /lib:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF" ^
  /lib:"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF" ^
  /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll ^
  /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:System.dll /r:System.Core.dll "%~dp0Snow.cs"
if errorlevel 1 (
    echo [ERROR] Build failed. See messages above.
    echo If it said "file is in use / zanyat drugoy programoy":
    echo   1) tray icon near clock -^> DesktopSnow -^> Exit
   2) or Task Manager -^> kill DesktopSnow.exe, then run build.bat again
    echo.
    pause
    exit /b 1
)

echo Step 4: build OK, launching...
if not exist "%~dp0DesktopSnow.exe" (
    echo [ERROR] exe was not created - something blocked writing (antivirus?).
    pause
    exit /b 1
)
start "" "%~dp0DesktopSnow.exe"
echo === Done. Snow should start. Close this window. ===
pause
exit /b 0
