@echo off
rem Builds NetSpeedWidget.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find csc.exe from .NET Framework 4.
  echo Open "Turn Windows features on or off", tick ".NET Framework 4.8 Advanced Services", then run this again.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:NetSpeedWidget.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  NetSpeedWidget.cs
if errorlevel 1 (
  echo.
  echo Build failed - see the errors above.
  pause
  exit /b 1
)

echo.
echo Built NetSpeedWidget.exe - double-click it to start.
pause
