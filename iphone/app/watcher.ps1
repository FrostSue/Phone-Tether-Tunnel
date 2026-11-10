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

# Windows balloon notification (separate hidden process, so the watcher never blocks).
function Notify([string]$title, [string]$text) {
    $t = $title.Replace("'", "''"); $x = $text.Replace("'", "''")
    $cmd = "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; `$n = New-Object System.Windows.Forms.NotifyIcon; " +
           "`$n.Icon = [System.Drawing.SystemIcons]::Information; `$n.Visible = `$true; `$n.ShowBalloonTip(15000, '$t', '$x', 'Info'); Start-Sleep 16; `$n.Dispose()"
    Start-Process powershell.exe -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', $cmd -WindowStyle Hidden
}

# Why the phone's share was not found, as a code and a plain instruction.
function Get-MissReason {
    $wifi = @(Get-PhysicalAdapters)
    if (-not $wifi) { return @('nolink', 'Bilgisayar hicbir aga bagli degil. iPhone Wi-Fi''sine (veya USB''ye) baglanin.') }
    $gws = @(Get-Gateways)
    if (-not $gws) {
        return @('noip', 'iPhone bilgisayara IPv4 adresi vermiyor ve sabit adrese de cevap vermiyor. Telefonda: Shadowrocket > Ayarlar > Tunnel > "Include All Networks" KAPALI olmali; sonra Kisisel Erisim Noktasi''ni kapatip acin.')
    }
    if (-not ($gws | Where-Object Ip -eq $HotspotGw)) {
        return @('other', "Bilgisayar iPhone hotspot'una degil baska bir aga bagli (ag gecidi $($gws[0].Ip)). iPhone Wi-Fi'sine baglanin.")
    }
    foreach ($g in ($gws | Where-Object Ip -eq $HotspotGw)) {
        if (Test-Connection $g.Ip -Count 2 -Quiet) {
            return @('noshare', "Telefona ulasildi ($($g.Ip)) ama Shadowrocket proxy paylasimi kapali. Telefonda: Shadowrocket VPN'i kapatip acin; Ayarlar > Proxy > Proxy Share acik olmali ve IP olarak 172.20.10.1 gostermeli.")
        }
    }
    return @('nophone', "Ag gecidi ($($gws[0].Ip)) cevap vermiyor. Bu bir iPhone hotspot'u degil ya da telefonun hotspot'u takildi: Kisisel Erisim Noktasi'ni kapatip acin.")
}

function Stop-Tunnel {
    Get-OwnProcess $SbExe | Stop-Process -Force
    Get-OwnProcess $XrayExe | Stop-Process -Force
    Remove-Item $XrayCfg -Force   # the dashboard reads the phone address from it; never show a stale one
    Start-Sleep -Milliseconds 500
    Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Where-Object { $_.InterfaceAlias -match 'tun|sing' -or $_.NextHop -match '^172\.19\.' } | Remove-NetRoute -Confirm:$false
    Clear-DnsClientCache
}

Set-Content (Join-Path $AppDir 'watcher.pid') $PID
Log "watcher basladi (PID $PID, killSwitch: $killSwitch). Denenecek portlar: $($Ports -join ', ')"
$currentKey = $null; $currentPort = 0; $currentIp = ''; $missing = $false; $missReason = ''
$lastRenew = [datetime]::MinValue; $lastSeen = $null; $missCount = 0

if ($killSwitch) { Ensure-SingBox }   # bring the TUN up first so nothing goes out directly while we search

while ($true) {
    $r = Restore-HotspotFallback -IfStale
    if ($r) { Log $r }
    # The phone in use answers slowly while every app reconnects through it; a
    # short probe then fails and used to tear the tunnel down. Probe it patiently
    # and only treat it as gone after 3 failed cycles in a row.
    $best = $null
    # After a network switch the old address is on no connected network any more:
    # drop it at once instead of probing it patiently (that delayed the switch ~1 min).
    if ($currentKey -and -not (Get-PhoneCandidates | Where-Object Ip -eq $currentIp)) {
        Log "son telefon adresi ($currentIp) artik bagli aglarda yok - ag degisti, telefon yeniden araniyor"
        $currentIp = ''; $missCount = 2
    }
    if ($currentKey -and $currentIp) {
        for ($k = 0; $k -lt 3 -and -not $best; $k++) {
            if (Test-Socks $currentIp $currentPort 3000) { $best = [pscustomobject]@{ Ip = $currentIp; Port = $currentPort; Alias = 'son adres' } }
        }
    }
    if (-not $best) { $best = Find-Phone $currentPort $currentIp }
    if ($best) { $missCount = 0 }
    elseif ($currentKey -and ++$missCount -lt 3) { Start-Sleep -Seconds $interval; continue }
    if (-not $best -and ((Get-Date) - $lastRenew).TotalSeconds -ge 20) {
        $lastRenew = Get-Date
        $fix = @(Repair-Dhcp)
        foreach ($l in $fix) { Log "IPv4 adresi yoktu (169.254.x) -> $l" }
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
        if ($missing) { Notify 'PhoneVPN baglandi' "Telefon bulundu ($($best.Ip):$($best.Port)), tum trafik tunelden gidiyor." }
        $missing = $false; $missReason = ''
        Ensure-SingBox
    } else {
        $why = Get-MissReason
        if (-not $missing -or $why[0] -ne $missReason) {
            Log "telefonun proxy paylasimi bulunamadi: $($why[1])"
            if (-not $missing -and -not $killSwitch) { Log "killSwitch kapali: $graceSec sn icinde bulunamazsa tunel kapatilir, internet dogrudan calisir" }
            Notify 'PhoneVPN: telefona baglanilamadi' $why[1]
            $missing = $true; $missReason = $why[0]
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
