$ErrorActionPreference = 'SilentlyContinue'
$app = $PSScriptRoot
$pidFile = Join-Path $app 'watcher.pid'
if (Test-Path $pidFile) {
    $w = Get-Process -Id ([int](Get-Content $pidFile)) | Where-Object { $_.Name -eq 'powershell' }
    if ($w) { $w | Stop-Process -Force }
    Remove-Item $pidFile -Force
}
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains((Join-Path $app 'watcher.ps1')) -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
$bin = Join-Path $app 'bin'
Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force

Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Where-Object { $_.InterfaceAlias -match 'tun|sing' -or $_.NextHop -match '^172\.19\.' } | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
Get-NetAdapter | Where-Object { $_.Name -match '^(phonevpn-tun|android-tun)$' -or $_.InterfaceDescription -match 'sing-tun' } | Disable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue

$ie = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$s = Get-ItemProperty $ie -ErrorAction SilentlyContinue
if ($s -and $s.ProxyEnable -eq 1 -and $s.ProxyServer -match '127\.0\.0\.1|localhost') {
    Set-ItemProperty $ie -Name ProxyEnable -Value 0 -ErrorAction SilentlyContinue
    Set-ItemProperty $ie -Name ProxyServer -Value '' -ErrorAction SilentlyContinue
}
Clear-DnsClientCache -ErrorAction SilentlyContinue

# The iPhone hotspot often leaves Wi-Fi/USB with only a 169.254.x.x address after
# the phone, its hotspot or Shadowrocket restarts; without a fix the PC stays
# offline even with PhoneVPN stopped. Both steps have short timeouts.
. (Join-Path $app 'common.ps1')
$r = Restore-HotspotFallback -IfStale; if ($r) { Write-Host $r }
foreach ($l in @(Repair-Dhcp)) { Write-Host "IPv4 adresi yoktu -> $l" }
