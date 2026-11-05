# Stops PhoneVPN (watcher, Xray, sing-box). Only touches PhoneVPN's own processes.
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
