# Android VPN watcher (Exclave): finds the phone (Wi-Fi hotspot or USB tethering),
# points Xray at it, keeps Xray + sing-box running and closes connections that
# bypass the tunnel.
$ErrorActionPreference = 'SilentlyContinue'
$dir   = $PSScriptRoot
$xdir  = Join-Path $dir 'xray'
$xcfg  = Join-Path $xdir 'config.json'
$sbExe = Join-Path $dir 'android-singbox.exe'
$xExe  = Join-Path $xdir 'android-xray.exe'
$log   = Join-Path $dir 'watcher.log'
$Port  = 2080         # Exclave SOCKS5 port on the phone
$LocalPort = 17880    # local Xray SOCKS port used by sing-box (must match config.json)
$IntervalSec = 5

function Log($m) {
    Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $m)
    if ((Get-Item $log).Length -gt 512KB) { $t = Get-Content $log -Tail 300; Set-Content $log $t }
}

# A plain TCP connect is not enough (the TUN stack accepts any SYN), so do a
# real SOCKS5 greeting and require the "05 00" answer.
function Test-Port($ip) {
    $c = New-Object Net.Sockets.TcpClient
    try {
        $r = $c.BeginConnect($ip, $Port, $null, $null)
        if (-not ($r.AsyncWaitHandle.WaitOne(800) -and $c.Connected)) { return $false }
        $s = $c.GetStream(); $s.ReadTimeout = 1500
        $s.Write([byte[]](5, 1, 0), 0, 3)
        $b = New-Object byte[] 2
        $n = $s.Read($b, 0, 2)
        return ($n -eq 2 -and $b[0] -eq 5 -and $b[1] -eq 0)
    } catch { return $false } finally { $c.Close() }
}

# Every physical default gateway is a candidate; the one with the lowest metric
# that answers on the proxy port wins (USB tethering usually beats Wi-Fi).
function Find-Phone {
    $up = @(Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { $_.ifIndex })
    $routes = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
        Where-Object { $_.NextHop -ne '0.0.0.0' -and $_.InterfaceAlias -notmatch 'tun|wintun|sing|clash|v2ray' -and $up -contains $_.ifIndex }
    $cands = foreach ($r in $routes) {
        $if = Get-NetIPInterface -InterfaceIndex $r.ifIndex -AddressFamily IPv4
        [pscustomobject]@{ Ip = $r.NextHop; Alias = $r.InterfaceAlias; Metric = $r.RouteMetric + $if.InterfaceMetric }
    }
    foreach ($c in ($cands | Sort-Object Metric)) { if (Test-Port $c.Ip) { return $c } }
    return $null
}

function Write-XrayConfig($ip) {
    $json = @"
{
  "log": { "loglevel": "warning", "error": "xray-error.log" },
  "inbounds": [
    { "tag": "local-in", "listen": "127.0.0.1", "port": $LocalPort, "protocol": "socks",
      "settings": { "auth": "noauth", "udp": true, "ip": "127.0.0.1" } }
  ],
  "outbounds": [
    { "tag": "phone", "protocol": "socks",
      "settings": { "servers": [ { "address": "$ip", "port": $Port } ] } }
  ]
}
"@
    [IO.File]::WriteAllText($xcfg, $json, (New-Object Text.UTF8Encoding($false)))
}

function Get-Own($name) { Get-Process -Name $name -ErrorAction SilentlyContinue }

function Start-Xray {
    Get-Own 'android-xray' | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    Start-Process $xExe -ArgumentList "run -c `"$xcfg`"" -WorkingDirectory $xdir -WindowStyle Hidden
}

function Ensure-SingBox {
    if (-not (Get-Own 'android-singbox')) {
        Log 'sing-box calismiyor, baslatiliyor'
        Start-Process $sbExe -ArgumentList "run -D `"$dir`" -c `"$(Join-Path $dir 'config.json')`"" -WorkingDirectory $dir -WindowStyle Hidden
    }
}

# --- Leak cleanup -----------------------------------------------------------
# TCP connections opened while the TUN was down (restart gaps) stay bound to the
# physical adapter and keep using plain mobile data even after the TUN is back.
# Close them (SetTcpEntry DELETE_TCB); apps reconnect through the TUN.
if (-not ('PvTcp' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PvTcp {
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW { public uint dwState; public uint dwLocalAddr; public uint dwLocalPort; public uint dwRemoteAddr; public uint dwRemotePort; }
    [DllImport("iphlpapi.dll")] static extern uint SetTcpEntry(ref MIB_TCPROW row);
    static uint Addr(string ip) { return BitConverter.ToUInt32(System.Net.IPAddress.Parse(ip).GetAddressBytes(), 0); }
    static uint Port(int p) { return (uint)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF)); }
    public static uint Close(string la, int lp, string ra, int rp) {
        MIB_TCPROW r = new MIB_TCPROW();
        r.dwState = 12; r.dwLocalAddr = Addr(la); r.dwLocalPort = Port(lp); r.dwRemoteAddr = Addr(ra); r.dwRemotePort = Port(rp);
        return SetTcpEntry(ref r);
    }
}
'@
}

function Test-PrivateIPv4([string]$ip) {
    return ($ip -match '^(0\.|10\.|127\.|169\.254\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.|100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.|2(2[4-9]|[3-5]\d)\.)')
}

function Reset-LeakedTcp {
    $phys = @(Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Name -notmatch 'tun|wintun|sing|clash|v2ray' } |
        ForEach-Object { (Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4).IPAddress })
    if (-not $phys) { return @() }
    $out = @()
    foreach ($c in (Get-NetTCPConnection -State Established)) {
        if ($phys -notcontains $c.LocalAddress) { continue }
        if ($c.RemoteAddress -notmatch '^\d+\.\d+\.\d+\.\d+$' -or (Test-PrivateIPv4 $c.RemoteAddress)) { continue }
        $rc = [PvTcp]::Close($c.LocalAddress, $c.LocalPort, $c.RemoteAddress, $c.RemotePort)
        $pn = (Get-Process -Id $c.OwningProcess).Name
        $out += "$pn -> $($c.RemoteAddress):$($c.RemotePort)$(if ($rc -ne 0) { " (kapatilamadi, kod $rc)" })"
    }
    return $out
}

Set-Content (Join-Path $dir 'watcher.pid') $PID
Log "watcher basladi (PID $PID)"
Ensure-SingBox   # bring the TUN up first so nothing goes out directly while we search
$current = $null
$missing = $false

while ($true) {
    $best = Find-Phone
    if ($best) {
        if ($best.Ip -ne $current) {
            Log "telefon bulundu: $($best.Ip) ($($best.Alias)) -> Xray bu adrese yonlendirildi"
            Write-XrayConfig $best.Ip
            Start-Xray
            $current = $best.Ip
        } elseif (-not (Get-Own 'android-xray')) {
            Log 'xray kapanmis, yeniden baslatiliyor'
            Start-Xray
        }
        $missing = $false
    } elseif (-not $missing) {
        Log "telefona ulasilamiyor (son adres: $current), bekleniyor"
        $missing = $true
    }
    Ensure-SingBox
    if (Get-Own 'android-singbox') {
        foreach ($l in (Reset-LeakedTcp)) { Log "tuneli atlayan baglanti kapatildi: $l" }
    }
    Start-Sleep -Seconds $IntervalSec
}
