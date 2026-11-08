# PhoneVPN network self-heal. Run by the "PhoneVPN iPhone Netfix" scheduled task
# at startup and whenever Windows connects to a network (as SYSTEM), so the PC
# is never left offline by PhoneVPN leftovers or by the iPhone hotspot's DHCP:
#  - when PhoneVPN is not running: removes leftover TUN routes / system proxy,
#  - undoes the static hotspot fallback once the adapter is on another network,
#  - fixes a 169.254.x.x (no DHCP) address: renew, then iPhone hotspot fallback.
# "netfix.ps1 -Register" (as administrator) creates/updates that scheduled task.
param([switch]$Register)
$ErrorActionPreference = 'SilentlyContinue'
$app = $PSScriptRoot

if ($Register) {
    $taskName = 'PhoneVPN iPhone Netfix'
    $self = Join-Path $app 'netfix.ps1'
    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>PhoneVPN (iPhone): removes leftovers and repairs the hotspot IPv4 address at startup and on network connect.</Description></RegistrationInfo>
  <Triggers>
    <BootTrigger><Enabled>true</Enabled><Delay>PT20S</Delay></BootTrigger>
    <EventTrigger><Enabled>true</Enabled><Delay>PT3S</Delay>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="Microsoft-Windows-NetworkProfile/Operational"&gt;&lt;Select Path="Microsoft-Windows-NetworkProfile/Operational"&gt;*[System[(EventID=10000)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
    </EventTrigger>
  </Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>powershell.exe</Command><Arguments>-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "$self"</Arguments></Exec>
  </Actions>
</Task>
"@
    $f = Join-Path $env:TEMP 'phonevpn-netfix-task.xml'
    [IO.File]::WriteAllText($f, $xml, [Text.Encoding]::Unicode)
    schtasks.exe /Create /F /TN $taskName /XML $f | Out-Null
    Remove-Item $f -Force
    if (Get-ScheduledTask -TaskName $taskName) { Write-Host "Ag onarim gorevi kuruldu: $taskName (acilista ve her ag baglantisinda)" }
    else { Write-Host "UYARI: '$taskName' gorevi kurulamadi (yonetici olarak calistirin)." -ForegroundColor Yellow }
    return
}
. (Join-Path $app 'common.ps1')
$log = Join-Path $AppDir 'netfix.log'
function Log($m) {
    Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $m)
    if ((Get-Item $log).Length -gt 256KB) { $t = Get-Content $log -Tail 200; Set-Content $log $t }
}

Start-Sleep -Seconds 5   # let Windows finish its own DHCP after the connect event

$myWatcher = Join-Path $AppDir 'watcher.ps1'
$watcherUp = [bool](Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($myWatcher) })

if (-not $watcherUp) {
    # PhoneVPN is not running: nothing of it may remain in the network stack.
    if ((Get-OwnProcess $SbExe) -or (Get-OwnProcess $XrayExe)) {
        Get-OwnProcess $SbExe | Stop-Process -Force; Get-OwnProcess $XrayExe | Stop-Process -Force
        Log 'watcher yokken calisan sing-box/xray kapatildi'
    }
    $stale = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Where-Object { $_.InterfaceAlias -match 'tun|sing' -or $_.NextHop -match '^172\.19\.' })
    if ($stale) { $stale | Remove-NetRoute -Confirm:$false; Log 'kalan TUN rotalari silindi' }
}

$r = Restore-HotspotFallback -IfStale
if ($r) { Log $r }
if (-not $watcherUp) {   # the watcher does its own repair while it runs
    foreach ($l in @(Repair-Dhcp)) { Log "IPv4 adresi yoktu (169.254.x) -> $l" }
}
