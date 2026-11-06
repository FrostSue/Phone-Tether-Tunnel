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
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -match 'PhoneVPN[^"]*\\watcher\.ps1' -and $_.CommandLine -notlike "*$me*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Get-Process | Where-Object { $_.Name -match '^(phonevpn-singbox|phonevpn-xray|Proxifier|v2rayN|sing-box|xray|mihomo|clash.*|tun2socks)$' } |
        Stop-Process -Force
}

Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Where-Object { $_.InterfaceAlias -match 'tun|sing' -or $_.NextHop -match '^172\.19\.' } | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
Get-NetAdapter | Where-Object { $_.Name -match '^(phonevpn-tun|android-tun)$' -or $_.InterfaceDescription -match 'sing-tun' } | Disable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue

$ie = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$s = Get-ItemProperty $ie -ErrorAction SilentlyContinue
if ($s -and $s.ProxyEnable -eq 1 -and $s.ProxyServer -match '127\.0\.0\.1|localhost') {
    Set-ItemProperty $ie -Name ProxyEnable -Value 0 -ErrorAction SilentlyContinue
    Set-ItemProperty $ie -Name ProxyServer -Value '' -ErrorAction SilentlyContinue
}
Clear-DnsClientCache -ErrorAction SilentlyContinue
