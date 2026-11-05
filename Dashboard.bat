@echo off
setlocal
cd /d "%~dp0"

if not exist "%~dp0PhoneVPNDashboard.exe" (
    echo Compiling PhoneVPNDashboard.exe...
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /lib:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF" /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:System.dll /out:"%~dp0PhoneVPNDashboard.exe" "%~dp0ui\PhoneVPNDashboard.cs"
)

start "" "%~dp0PhoneVPNDashboard.exe"
endlocal
