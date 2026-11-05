# PhoneVPN watcher: finds the phone's proxy (Wi-Fi hotspot or USB), points Xray
# at it, follows IP/port changes, and keeps Xray + sing-box running.
$ErrorActionPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'common.ps1')
$log = Join-Path $AppDir 'watcher.log'
$interval = [int]$Settings.intervalSec; if ($interval -lt 2) { $interval = 5 }

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

Set-Content (Join-Path $AppDir 'watcher.pid') $PID
Log "watcher basladi (PID $PID). Denenecek portlar: $($Ports -join ', ')"
$currentKey = $null; $currentPort = 0; $missing = $false

Ensure-SingBox   # bring the TUN up first so nothing goes out directly while we search

while ($true) {
    $best = Find-Phone $currentPort
    if ($best) {
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
            $currentKey = $key; $currentPort = $best.Port
        } elseif (-not (Get-OwnProcess $XrayExe)) {
            Log 'xray kapanmis, yeniden baslatiliyor'
            Start-Xray
        }
        $missing = $false
    } elseif (-not $missing) {
        Log "telefona ulasilamiyor (son adres: $currentKey), bekleniyor. Telefonda Proxy Share acik mi?"
        $missing = $true
    }
    Ensure-SingBox
    if (Get-OwnProcess $SbExe) {
        foreach ($l in (Reset-LeakedTcp)) { Log "tuneli atlayan baglanti kapatildi: $l" }
    }
    Start-Sleep -Seconds $interval
}
