@echo off
setlocal
cd /d "%~dp0"

rem Rebuilds PhoneVPNDashboard.exe with the .NET Framework compiler that ships with Windows
rem whenever a file in ui\ is newer than the exe, then starts it.

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

powershell -NoProfile -Command "$e='PhoneVPNDashboard.exe'; if(-not (Test-Path $e)){exit 1}; $t=(Get-Item $e).LastWriteTime; if(Get-ChildItem ui -File | Where-Object { $_.LastWriteTime -gt $t }){exit 1}; exit 0"
if not errorlevel 1 goto run

echo Building PhoneVPNDashboard.exe ...
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /lib:"%FW%\WPF" ^
  /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /win32icon:"ui\app.ico" /resource:"ui\MainWindow.xaml",MainWindow.xaml /resource:"ui\app.ico",app.ico ^
  /out:"PhoneVPNDashboard.new.exe" "ui\*.cs" > build.log 2>&1
if errorlevel 1 (
    type build.log
    echo.
    echo Build failed. See build.log.
    if exist "PhoneVPNDashboard.exe" ( echo Starting the previous build. & goto run )
    pause
    exit /b 1
)
move /y "PhoneVPNDashboard.new.exe" "PhoneVPNDashboard.exe" >nul 2>&1
if errorlevel 1 (
    echo The dashboard is still running - close it and start Dashboard.bat again to use the new build.
    del "PhoneVPNDashboard.new.exe" >nul 2>&1
    pause
)

:run
start "" "%~dp0PhoneVPNDashboard.exe"
endlocal
