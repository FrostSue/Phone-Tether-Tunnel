# Starts the Android VPN: stops conflicting tools, then launches the watcher hidden.
$ErrorActionPreference = 'SilentlyContinue'
$dir = $PSScriptRoot
& (Join-Path $dir 'stop.ps1') -forStart
$ie = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$s = Get-ItemProperty $ie
if ($s.ProxyEnable -eq 1 -and $s.ProxyServer -match '127\.0\.0\.1|localhost') {
    Set-ItemProperty $ie -Name ProxyEnable -Value 0
    Write-Host "Windows sistem proxy ayari ($($s.ProxyServer)) kapatildi."
}
Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $dir 'watcher.ps1')`"" -WindowStyle Hidden
Write-Host 'Android VPN baslatildi. Telefon (Wi-Fi Hotspot veya USB) otomatik bulunuyor.'
