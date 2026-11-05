# PhoneVPN diagnostics: finds the phone, checks TCP and UDP through its proxy,
# and (if PhoneVPN is running) checks the whole-PC tunnel.
$ErrorActionPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'common.ps1')
function Ok($m)   { Write-Host "  [OK]    $m" -ForegroundColor Green }
function Bad($m)  { Write-Host "  [HATA]  $m" -ForegroundColor Red }
function Info($m) { Write-Host "  $m" }

Write-Host ''
Write-Host '=== 1) Ag baglantilari ===' -ForegroundColor Cyan
$gws = @(Get-Gateways)
if (-not $gws) { Bad 'Hicbir ag gecidi yok. Bilgisayar telefonun Hotspot/USB agina bagli mi?' }
foreach ($g in $gws) { Info "$($g.Alias): ag gecidi $($g.Ip)" }

Write-Host ''
Write-Host '=== 2) Telefonun proxy portu ===' -ForegroundColor Cyan
$ph = Find-Phone
if (-not $ph) {
    Bad "Telefonda proxy bulunamadi (denenen portlar: $($Ports -join ', '))."
    Info 'Shadowrocket > Ayarlar > Proxy > Proxy Share acik mi? Port numarasi listede yoksa'
    Info 'app\settings.json icindeki "ports" listesine ekleyin.'
    Write-Host ''; return
}
Ok "Telefon bulundu: $($ph.Ip):$($ph.Port) ($($ph.Alias))"

Write-Host ''
Write-Host '=== 3) Telefon proxy testi (TCP / UDP) ===' -ForegroundColor Cyan
$auth = ''; if ($User) { $auth = "${User}:${Pass}@" }
$tcpIp = (curl.exe -s -m 15 --socks5-hostname "$auth$($ph.Ip):$($ph.Port)" https://api.ipify.org)
if ($tcpIp) { Ok "TCP calisiyor. Cikis IP: $tcpIp" } else { Bad 'TCP calismiyor.' }
$udpIp = Test-SocksUdp $ph.Ip $ph.Port
if ($udpIp) { Ok "UDP calisiyor (Discord sesi icin gerekli). UDP cikis IP: $udpIp" }
else {
    $hasV = $false; try { $hasV = [bool](Get-VlessOutbound) } catch {}
    if ($hasV) { Info 'Telefonun paylasimi UDP tasimiyor (Shadowrocket icin normal) - UDP, vless.txt zinciri ile gidecek.' }
    else { Bad 'Telefonun proxy portu UDP tasimiyor. Discord sesi icin app\vless.txt dosyasina sunucu linkinizi yapistirin.' }
}

Write-Host ''
Write-Host '=== 4) PhoneVPN (tum bilgisayar) ===' -ForegroundColor Cyan
$sb = Get-OwnProcess $SbExe; $xr = Get-OwnProcess $XrayExe
if (-not ($sb -and $xr)) {
    Info 'PhoneVPN su an calismiyor. "PhoneVPN Baslat" ile baslatip bu testi tekrar calistirin.'
} else {
    Ok 'sing-box ve Xray calisiyor.'
    $sysIp = (curl.exe -s -m 15 https://api.ipify.org)
    if ($sysIp -and $sysIp -eq $tcpIp) { Ok "Bilgisayarin TCP trafigi tunelden gidiyor: $sysIp" }
    elseif ($sysIp) { Bad "Bilgisayarin cikis IP'si ($sysIp) telefon proxy'sininkinden ($tcpIp) farkli." }
    else { Bad 'Bilgisayardan internete erisilemiyor.' }
    $v = $null; try { $v = Get-VlessOutbound } catch {}
    if ($v) { Ok "VLESS zinciri tanimli (vless.txt, sunucu $($v.settings.vnext[0].address):$($v.settings.vnext[0].port), mod: $VlessMode)" }
    else { Info 'vless.txt bos: UDP icin VLESS zinciri kullanilmiyor.' }
    $sysUdp = Test-DirectStun
    if ($sysUdp) { Ok "Bilgisayarin UDP trafigi tunelden gidiyor (Discord sesi calisir): $sysUdp" }
    elseif ($v) { Bad 'UDP gecmiyor. watcher.log ve xray-error.log dosyalarina bakin.' }
    else { Bad 'Bilgisayarin UDP trafigi gecmiyor (Discord sesi icin vless.txt doldurun).' }
}
$wl = Join-Path $AppDir 'watcher.log'
if (Test-Path $wl) { Write-Host ''; Write-Host '=== Son izleyici kayitlari ===' -ForegroundColor Cyan; Get-Content $wl -Tail 5 | ForEach-Object { Info $_ } }
Write-Host ''
