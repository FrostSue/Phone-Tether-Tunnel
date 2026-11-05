# Android VPN diagnostics.
$ErrorActionPreference = 'SilentlyContinue'
$dir = $PSScriptRoot
function Ok($m)   { Write-Host "  [OK]    $m" -ForegroundColor Green }
function Bad($m)  { Write-Host "  [HATA]  $m" -ForegroundColor Red }
function Info($m) { Write-Host "  $m" }

function Test-Socks($ip) {
    $c = New-Object Net.Sockets.TcpClient
    try {
        $r = $c.BeginConnect($ip, 2080, $null, $null)
        if (-not ($r.AsyncWaitHandle.WaitOne(800) -and $c.Connected)) { return $false }
        $s = $c.GetStream(); $s.ReadTimeout = 1500; $s.Write([byte[]](5, 1, 0), 0, 3)
        $b = New-Object byte[] 2; $n = $s.Read($b, 0, 2); return ($n -eq 2 -and $b[0] -eq 5 -and $b[1] -eq 0)
    } catch { return $false } finally { $c.Close() }
}
function Get-StunIp {
    $s = New-Object Net.Sockets.UdpClient; $s.Client.ReceiveTimeout = 3000
    try {
        $ip = [Net.Dns]::GetHostAddresses('stun.l.google.com') | Where-Object AddressFamily -eq 'InterNetwork' | Select-Object -First 1
        $tid = New-Object byte[] 12; (New-Object Random).NextBytes($tid); $req = [byte[]](0, 1, 0, 0, 0x21, 0x12, 0xA4, 0x42) + $tid
        for ($k = 0; $k -lt 3; $k++) {
            [void]$s.Send($req, $req.Length, (New-Object Net.IPEndPoint($ip, 19302)))
            try {
                $ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0); $r = $s.Receive([ref]$ep); $i = 20
                while ($i + 4 -le $r.Length) { $t = $r[$i] * 256 + $r[$i + 1]; $l = $r[$i + 2] * 256 + $r[$i + 3]
                    if ($t -eq 0x20) { return (($r[$i + 8] -bxor 0x21), ($r[$i + 9] -bxor 0x12), ($r[$i + 10] -bxor 0xA4), ($r[$i + 11] -bxor 0x42)) -join '.' }
                    $i += 4 + $l + ((4 - $l % 4) % 4) }
                return 'cevap alindi'
            } catch {}
        }
        return $null
    } finally { $s.Close() }
}

Write-Host ''; Write-Host '=== Android VPN testi ===' -ForegroundColor Cyan
$up = @(Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { $_.ifIndex })
$gws = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | Where-Object { $up -contains $_.ifIndex -and $_.InterfaceAlias -notmatch 'tun|sing|clash|v2ray' -and $_.NextHop -ne '0.0.0.0' }
$phone = $null
foreach ($g in $gws) { Info "$($g.InterfaceAlias): ag gecidi $($g.NextHop)"; if (-not $phone -and (Test-Socks $g.NextHop)) { $phone = $g } }
if ($phone) { Ok "Telefon bulundu: $($phone.NextHop):2080 ($($phone.InterfaceAlias))" }
else { Bad 'Telefonda 2080 portu bulunamadi. Exclave acik mi, "Allow Connections from the LAN" acik mi?' }

$sb = Get-Process android-singbox; $xr = Get-Process android-xray
if ($sb -and $xr) {
    Ok 'sing-box ve Xray calisiyor.'
    $ipT = curl.exe -s -m 15 https://api.ipify.org
    if ($ipT) { Ok "TCP tunelden gidiyor: $ipT" } else { Bad 'TCP: internete erisilemiyor.' }
    $ipU = Get-StunIp
    if ($ipU) { Ok "UDP tunelden gidiyor (Discord sesi calisir): $ipU" } else { Bad 'UDP gecmiyor.' }
} else { Info 'Android VPN su an calismiyor. "VPN-Baslat" ile baslatin.' }

$wl = Join-Path $dir 'watcher.log'
if (Test-Path $wl) { Write-Host ''; Write-Host '=== Son kayitlar ===' -ForegroundColor Cyan; Get-Content $wl -Tail 5 | ForEach-Object { Info $_ } }
Write-Host ''
