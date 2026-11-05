# Stops the Android VPN (watcher, Xray, sing-box).
# With -forStart it also stops everything that would fight over the network:
# the iPhone PhoneVPN system, Proxifier, v2rayN and other sing-box/xray cores.
param([switch]$forStart)
$ErrorActionPreference = 'SilentlyContinue'
$dir = $PSScriptRoot

$pidFile = Join-Path $dir 'watcher.pid'
if (Test-Path $pidFile) {
    Get-Process -Id ([int](Get-Content $pidFile)) | Where-Object { $_.Name -eq 'powershell' } | Stop-Process -Force
    Remove-Item $pidFile -Force
}
$me = Join-Path $dir 'watcher.ps1'
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($me) -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-Process android-singbox, android-xray | Stop-Process -Force

if ($forStart) {
    # iPhone PhoneVPN watcher(s) first, so they do not restart their cores.
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -match 'PhoneVPN[^"]*\\watcher\.ps1' -and $_.CommandLine -notlike "*$me*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Get-Process | Where-Object { $_.Name -match '^(phonevpn-singbox|phonevpn-xray|Proxifier|v2rayN|sing-box|xray|mihomo|clash.*|tun2socks)$' } |
        Stop-Process -Force
}
