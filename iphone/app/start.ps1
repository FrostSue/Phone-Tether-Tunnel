# Starts PhoneVPN. Stops tools that would fight over the network (v2rayN and its
# cores, Proxifier, other sing-box/xray TUN instances) first.
$ErrorActionPreference = 'SilentlyContinue'
$app = $PSScriptRoot
& (Join-Path $app 'stop.ps1')

$bin = Join-Path $app 'bin'
# Another VPN watcher (e.g. the Android/Exclave setup) would keep restarting its own TUN.
$myWatcher = Join-Path $app 'watcher.ps1'
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -and -not $_.CommandLine.Contains($myWatcher) -and
                   ($_.CommandLine -like '*\sing-box\watcher.ps1*' -or $_.CommandLine -match 'PhoneVPN[^"]*\\(app\\)?watcher\.ps1') } |
    ForEach-Object { Write-Host 'Diger VPN izleyicisi kapatiliyor'; Stop-Process -Id $_.ProcessId -Force }
$conflicts = Get-Process | Where-Object {
    ($_.Path -and $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase)) -eq $false -and (
        $_.Name -match '^(v2rayN|Proxifier|sing-box|xray|android-singbox|android-xray|mihomo|clash.*|v2ray|tun2socks)$' -or
        ($_.Name -match '^phonevpn-' -and $_.Path -and -not $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase)) -or
        $_.Path -like '*\v2rayN*')
}
foreach ($p in $conflicts) {
    Write-Host "Cakisan program kapatiliyor: $($p.Name)  ($($p.Path))"
    Stop-Process -Id $p.Id -Force
}

# v2rayN leaves the Windows system proxy pointing at 127.0.0.1 when it is killed.
$ie = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$s = Get-ItemProperty $ie
if ($s.ProxyEnable -eq 1 -and $s.ProxyServer -match '127\.0\.0\.1|localhost') {
    Set-ItemProperty $ie -Name ProxyEnable -Value 0
    Write-Host "Windows sistem proxy ayari ($($s.ProxyServer)) kapatildi (v2rayN kalintisi)."
}

Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $app 'watcher.ps1')`"" -WindowStyle Hidden
Write-Host ''
Write-Host 'PhoneVPN baslatildi. Telefon (Wi-Fi Hotspot veya USB) otomatik bulunuyor.'
Write-Host 'Durumu gormek icin masaustundeki "iPhone VPN Test" kisayolunu calistirabilirsiniz.'
