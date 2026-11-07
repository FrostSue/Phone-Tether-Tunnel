# PhoneVPN watcher: finds the phone's proxy (Wi-Fi hotspot or USB), points Xray
# at it, follows IP/port changes, and keeps Xray + sing-box running.
$ErrorActionPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'common.ps1')
$log = Join-Path $AppDir 'watcher.log'
$interval = [int]$Settings.intervalSec; if ($interval -lt 2) { $interval = 5 }
# killSwitch = true : the TUN is up all the time; with the phone's share gone nothing reaches the internet.
# killSwitch = false: the TUN is only up while the share is reachable (30 s grace for short drops);
#                     otherwise the PC uses the hotspot directly, so it is never left offline.
$killSwitch = [bool]$Settings.killSwitch
$graceSec = 30

function Log($m) {
    Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $m)
    if ((Get-Item $log).Length -gt 512KB) { $t = Get-Content $log -Tail 300; Set-Content $log $t }
}

function Start-Xray {
    Get-OwnProcess $XrayExe | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    Start-Process $XrayExe -ArgumentList "run -c `"$XrayCfg`"" -WorkingDirectory $AppDir -WindowStyle Hidden
}

function Ensure-SingBox {
    if (-not (Get-OwnProcess $SbExe)) {
        Log 'sing-box calismiyor, baslatiliyor'
        Start-Process $SbExe -ArgumentList "run -D `"$AppDir`" -c `"$SbCfg`"" -WorkingDirectory $AppDir -WindowStyle Hidden
    }
}

function Stop-Tunnel {
    Get-OwnProcess $SbExe | Stop-Process -Force
    Get-OwnProcess $XrayExe | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Where-Object { $_.InterfaceAlias -match 'tun|sing' -or $_.NextHop -match '^172\.19\.' } | Remove-NetRoute -Confirm:$false
    Clear-DnsClientCache
}

Set-Content (Join-Path $AppDir 'watcher.pid') $PID
Log "watcher basladi (PID $PID, killSwitch: $killSwitch). Denenecek portlar: $($Ports -join ', ')"
$currentKey = $null; $currentPort = 0; $currentIp = ''; $missing = $false
$lastRenew = [datetime]::MinValue; $lastSeen = $null

if ($killSwitch) { Ensure-SingBox }   # bring the TUN up first so nothing goes out directly while we search

while ($true) {
    $best = Find-Phone $currentPort $currentIp
    if (-not $best -and ((Get-Date) - $lastRenew).TotalSeconds -ge 20) {
        $lastRenew = Get-Date
        $fix = @(Repair-Dhcp)
        foreach ($l in $fix) { Log "IPv4 adresi yoktu (169.254.x), DHCP yenilendi -> $l" }
        if ($fix) { $best = Find-Phone $currentPort $currentIp }
    }
    if ($best) {
        $lastSeen = Get-Date
        $vTime = if (Test-Path $VlessFile) { (Get-Item $VlessFile).LastWriteTimeUtc.Ticks } else { 0 }
        $key = "$($best.Ip):$($best.Port)|$vTime"
        if ($key -ne $currentKey) {
            Log "telefon bulundu: $($best.Ip):$($best.Port) ($($best.Alias)) -> Xray bu adrese yonlendirildi"
            $pin = Update-VlessPin $best.Ip $best.Port
            if ($pin) { Log "sunucu sertifikasi sabitlendi (SHA256 $($pin.Substring(0,16))...)" }
            $hasVless = Write-XrayConfig $best.Ip $best.Port
            if ($hasVless) {
                $chk = & $XrayExe run -test -c $XrayCfg 2>&1 | Select-Object -Last 1
                if ("$chk" -notmatch 'Configuration OK') {
                    Log "VLESS linki kullanilamadi: $chk"
                    Log 'VLESS olmadan devam ediliyor (sadece TCP).'
                    [void](Write-XrayConfig $best.Ip $best.Port -NoVless); $hasVless = $false
                }
            }
            if ($hasVless) { Log "VLESS zinciri aktif (mod: $VlessMode) - UDP, VLESS icinde telefonun TCP paylasimindan gider" }
            elseif (-not (Get-VlessOutbound)) { Log 'vless.txt yok/bos - sadece TCP calisir (UDP/Discord sesi icin vless.txt doldurun)' }
            Start-Xray
            $currentKey = $key; $currentPort = $best.Port; $currentIp = $best.Ip
        } elseif (-not (Get-OwnProcess $XrayExe)) {
            Log 'xray kapanmis, yeniden baslatiliyor'
            Start-Xray
        }
        $missing = $false
        Ensure-SingBox
    } else {
        if (-not $missing) {
            Log "telefona ulasilamiyor (son adres: $currentKey), bekleniyor. Telefonda Shadowrocket bagli ve Proxy Share acik mi?"
            if (-not $killSwitch) { Log "killSwitch kapali: $graceSec sn icinde bulunamazsa tunel kapatilir, internet dogrudan calisir" }
            $missing = $true
        }
        if ($killSwitch) { Ensure-SingBox }
        elseif ((Get-OwnProcess $SbExe) -and (-not $lastSeen -or ((Get-Date) - $lastSeen).TotalSeconds -ge $graceSec)) {
            Log 'tunel kapatildi - internet su an telefonun hotspot''undan DOGRUDAN gidiyor (VPN degil). Paylasim bulununca tunel yeniden acilir.'
            Stop-Tunnel
            $currentKey = $null   # rebuild Xray config when the phone comes back
        }
    }
    if (Get-OwnProcess $SbExe) {
        foreach ($l in (Reset-LeakedTcp)) { Log "tuneli atlayan baglanti kapatildi: $l" }
    }
    Start-Sleep -Seconds $interval
}
