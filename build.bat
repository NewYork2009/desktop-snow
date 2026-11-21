@echo off
rem Build DesktopSnow.exe using the C# compiler bundled with Windows.

setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo C# compiler not found. .NET Framework 4.x is required ^(it ships with Windows 10/11^).
    pause
    exit /b 1
)

"%CSC%" /nologo /optimize+ /target:winexe /out:"%~dp0DesktopSnow.exe" ^
  /lib:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF" ^
  /lib:"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF" ^
  /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll ^
  /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:System.dll /r:System.Core.dll "%~dp0Snow.cs"

if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

echo Build OK: %~dp0DesktopSnow.exe
start "" "%~dp0DesktopSnow.exe"
exit /b 0
