# Shared helpers for PhoneVPN (dot-sourced by watcher.ps1 and test.ps1).
$AppDir   = $PSScriptRoot
$BinDir   = Join-Path $AppDir 'bin'
$SbExe    = Join-Path $BinDir 'phonevpn-singbox.exe'
$XrayExe  = Join-Path $BinDir 'phonevpn-xray.exe'
$SbCfg    = Join-Path $AppDir 'singbox.json'
$XrayCfg  = Join-Path $AppDir 'xray-runtime.json'
$LocalPort = 17890
$Settings = Get-Content (Join-Path $AppDir 'settings.json') -Raw | ConvertFrom-Json
$Ports    = @($Settings.ports | ForEach-Object { [int]$_ })
$User     = [string]$Settings.username
$Pass     = [string]$Settings.password

# --- Leak cleanup -----------------------------------------------------------
# TCP connections opened while the TUN was down (restart gaps) stay bound to the
# physical adapter and keep using the phone's plain mobile data even after the
# TUN is back. Close them (SetTcpEntry DELETE_TCB); apps reconnect via the TUN.
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

# Closes established IPv4 TCP connections that go from a physical adapter straight
# to a public address (i.e. bypass the tunnel). Returns "process -> ip:port" strings.
function Reset-LeakedTcp {
    $phys = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' -and $_.Name -notmatch 'tun|wintun|sing|clash|v2ray' } |
        ForEach-Object { (Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).IPAddress })
    if (-not $phys) { return @() }
    $out = @()
    foreach ($c in (Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue)) {
        if ($phys -notcontains $c.LocalAddress) { continue }
        if (Test-PrivateIPv4 $c.RemoteAddress) { continue }
        if ($c.RemoteAddress -notmatch '^\d+\.\d+\.\d+\.\d+$') { continue }
        $rc = [PvTcp]::Close($c.LocalAddress, $c.LocalPort, $c.RemoteAddress, $c.RemotePort)
        $pn = (Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue).Name
        $out += "$pn -> $($c.RemoteAddress):$($c.RemotePort)$(if ($rc -ne 0) { " (kapatilamadi, kod $rc)" })"
    }
    return $out
}

# Real SOCKS5 handshake (a bare TCP connect can be faked by TUN stacks).
function Test-Socks([string]$ip, [int]$port, [int]$timeoutMs = 700) {
    $c = New-Object Net.Sockets.TcpClient
    try {
        $r = $c.BeginConnect($ip, $port, $null, $null)
        if (-not ($r.AsyncWaitHandle.WaitOne($timeoutMs) -and $c.Connected)) { return $false }
        $s = $c.GetStream(); $s.ReadTimeout = 1500
        if ($User) { $s.Write([byte[]](5, 2, 0, 2), 0, 4) } else { $s.Write([byte[]](5, 1, 0), 0, 3) }
        $b = New-Object byte[] 2
        $n = $s.Read($b, 0, 2)
        return ($n -eq 2 -and $b[0] -eq 5 -and ($b[1] -eq 0 -or $b[1] -eq 2))
    } catch { return $false } finally { $c.Close() }
}

# Physical, connected default gateways, best (lowest metric) first.
function Get-Gateways {
    $up = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up' | ForEach-Object { $_.ifIndex })
    $routes = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Where-Object { $_.NextHop -ne '0.0.0.0' -and $up -contains $_.ifIndex -and $_.InterfaceAlias -notmatch 'tun|wintun|sing|clash|v2ray' }
    $list = foreach ($r in $routes) {
        $if = Get-NetIPInterface -InterfaceIndex $r.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
        [pscustomobject]@{ Ip = $r.NextHop; Alias = $r.InterfaceAlias; Metric = $r.RouteMetric + $if.InterfaceMetric }
    }
    $list | Sort-Object Metric
}

# Returns @{Ip; Port; Alias} of the phone's proxy, trying the last known port first.
function Find-Phone([int]$preferPort = 0) {
    $order = @()
    if ($preferPort) { $order += $preferPort }
    $order += ($Ports | Where-Object { $_ -ne $preferPort })
    foreach ($g in Get-Gateways) {
        foreach ($p in $order) {
            if (Test-Socks $g.Ip $p) { return [pscustomobject]@{ Ip = $g.Ip; Port = $p; Alias = $g.Alias } }
        }
    }
    return $null
}

$VlessFile = Join-Path $AppDir 'vless.txt'
$PinFile   = Join-Path $AppDir 'vless.pin'

# SHA256 of the server's TLS leaf certificate, fetched through the phone's proxy
# share. Xray 26 replaced allowInsecure with certificate pinning, so links that
# carry allowInsecure=1 (self-signed server certs) are pinned to this hash.
function Get-CertPin([string]$phoneIp, [int]$phonePort, [string]$dstHost, [int]$dstPort, [string]$sni) {
    $t = New-Object Net.Sockets.TcpClient
    try {
        $t.Connect($phoneIp, $phonePort); $s = $t.GetStream(); $s.ReadTimeout = 8000
        $s.Write([byte[]](5, 1, 0), 0, 3); $r = New-Object byte[] 2; [void]$s.Read($r, 0, 2)
        $ipo = $null
        if ([Net.IPAddress]::TryParse($dstHost, [ref]$ipo)) { $a = [byte[]](1) + $ipo.GetAddressBytes() }
        else { $hb = [Text.Encoding]::ASCII.GetBytes($dstHost); $a = [byte[]](3, $hb.Length) + $hb }
        $req = [byte[]](5, 1, 0) + $a + [byte[]]([math]::Floor($dstPort / 256), ($dstPort % 256))
        $s.Write($req, 0, $req.Length); $r = New-Object byte[] 64; [void]$s.Read($r, 0, 64)
        if ($r[1] -ne 0) { return $null }
        $script:pinLeaf = $null
        $cb = [Net.Security.RemoteCertificateValidationCallback] { param($a1, $c, $a3, $a4) $script:pinLeaf = $c.GetRawCertData(); $true }
        $ssl = New-Object Net.Security.SslStream($s, $false, $cb)
        $ssl.AuthenticateAsClient($(if ($sni) { $sni } else { $dstHost }))
        $ssl.Close()
        if (-not $script:pinLeaf) { return $null }
        return (([Security.Cryptography.SHA256]::Create().ComputeHash($script:pinLeaf) | ForEach-Object { $_.ToString('x2') }) -join '')
    } catch { return $null } finally { $t.Close() }
}

# Refreshes vless.pin (if the link needs pinning) via the phone. Keeps the old pin if the fetch fails.
function Update-VlessPin([string]$phoneIp, [int]$phonePort) {
    if (-not (Test-Path $VlessFile)) { return }
    $line = Get-Content $VlessFile | Where-Object { $_ -match '^\s*vless://' } | Select-Object -First 1
    if (-not $line -or $line -notmatch 'allowInsecure=(1|true)') { return }
    Add-Type -AssemblyName System.Web
    $u = [Uri]($line.Trim()); $q = [System.Web.HttpUtility]::ParseQueryString($u.Query)
    $sni = if ($q['sni']) { $q['sni'] } else { $q['peer'] }
    $pin = Get-CertPin $phoneIp $phonePort $u.Host $u.Port $sni
    if ($pin) { Set-Content $PinFile "$($u.Host):$($u.Port)=$pin" -Encoding ASCII }
    return $pin
}
$VlessMode = if ($Settings.vlessMode) { [string]$Settings.vlessMode } else { 'udp' }   # 'udp' or 'all'

# Reads the first vless:// link from vless.txt and turns it into an Xray outbound
# that is dialed THROUGH the phone's proxy share (dialerProxy). UDP then travels
# inside VLESS (xudp) over TCP, so the phone's share only has to carry TCP.
function Get-VlessOutbound {
    if (-not (Test-Path $VlessFile)) { return $null }
    $line = Get-Content $VlessFile | Where-Object { $_ -match '^\s*vless://' } | Select-Object -First 1
    if (-not $line) { return $null }
    Add-Type -AssemblyName System.Web
    $u = [Uri]($line.Trim())
    $q = [System.Web.HttpUtility]::ParseQueryString($u.Query)
    function P($k, $d = '') { $v = $q[$k]; if ($v) { $v } else { $d } }
    $id   = [Uri]::UnescapeDataString($u.UserInfo)
    $net  = (P 'type' 'tcp').ToLower(); if ($net -eq 'splithttp') { $net = 'xhttp' }
    $sec  = (P 'security' 'none').ToLower()
    $user = [ordered]@{ id = $id; encryption = (P 'encryption' 'none') }
    if (P 'flow') { $user.flow = P 'flow' }
    $stream = [ordered]@{ network = $net; security = $sec; sockopt = @{ dialerProxy = 'phone' } }
    $sni = P 'sni' (P 'peer' '')
    if ($sec -eq 'tls') {
        $t = [ordered]@{}
        if ($sni) { $t.serverName = $sni }
        if (P 'fp') { $t.fingerprint = P 'fp' }
        if (P 'alpn') { $t.alpn = @((P 'alpn') -split ',') }
        if ((P 'allowInsecure') -in '1', 'true') {
            $pinLine = if (Test-Path $PinFile) { Get-Content $PinFile | Where-Object { $_ -like "$($u.Host):$($u.Port)=*" } | Select-Object -First 1 }
            if ($pinLine) { $t.pinnedPeerCertSha256 = ($pinLine -split '=', 2)[1] }
        }
        $stream.tlsSettings = $t
    } elseif ($sec -eq 'reality') {
        $r = [ordered]@{ serverName = $sni; fingerprint = (P 'fp' 'chrome'); publicKey = (P 'pbk'); shortId = (P 'sid') }
        if (P 'spx') { $r.spiderX = P 'spx' }
        $stream.realitySettings = $r
    }
    $path = P 'path'; $hostH = P 'host'
    switch ($net) {
        'ws'          { $w = [ordered]@{ path = $(if ($path) { $path } else { '/' }) }; if ($hostH) { $w.host = $hostH }; $stream.wsSettings = $w }
        'httpupgrade' { $w = [ordered]@{ path = $(if ($path) { $path } else { '/' }) }; if ($hostH) { $w.host = $hostH }; $stream.httpupgradeSettings = $w }
        'xhttp'       { $w = [ordered]@{ path = $(if ($path) { $path } else { '/' }) }; if ($hostH) { $w.host = $hostH }; if (P 'mode') { $w.mode = P 'mode' }; $stream.xhttpSettings = $w }
        'grpc'        { $stream.grpcSettings = [ordered]@{ serviceName = (P 'serviceName'); multiMode = ((P 'mode') -eq 'multi') } }
        'tcp'         { if ((P 'headerType') -eq 'http') {
                            $req = [ordered]@{ path = @($(if ($path) { $path } else { '/' })) }
                            if ($hostH) { $req.headers = @{ Host = @($hostH -split ',') } }
                            $stream.tcpSettings = @{ header = [ordered]@{ type = 'http'; request = $req } } } }
    }
    return [ordered]@{
        tag = 'vless'; protocol = 'vless'
        settings = @{ vnext = @(@{ address = $u.Host; port = $u.Port; users = @($user) }) }
        streamSettings = $stream
    }
}

function Write-XrayConfig([string]$ip, [int]$port, [switch]$NoVless) {
    $server = @{ address = $ip; port = $port }
    if ($User) { $server.users = @(@{ user = $User; pass = $Pass }) }
    $outs = @(@{ tag = 'phone'; protocol = 'socks'; settings = @{ servers = @($server) } })
    $rules = @()
    $vless = $null
    if (-not $NoVless) { try { $vless = Get-VlessOutbound } catch { $vless = $null } }
    if ($vless) {
        if ($VlessMode -eq 'all') { $outs = @($vless) + $outs }   # first outbound = default
        else { $outs += $vless; $rules += @{ type = 'field'; network = 'udp'; outboundTag = 'vless' } }
    }
    $cfg = [ordered]@{
        log       = @{ loglevel = 'warning'; error = 'xray-error.log' }
        inbounds  = @(@{ tag = 'local-in'; listen = '127.0.0.1'; port = $LocalPort; protocol = 'socks'
                         settings = @{ auth = 'noauth'; udp = $true; ip = '127.0.0.1' } })
        outbounds = $outs
        routing   = @{ rules = $rules }
    }
    [IO.File]::WriteAllText($XrayCfg, ($cfg | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    return [bool]$vless
}

# Names are unique to PhoneVPN; Path is unreadable for elevated processes from a
# non-elevated shell, so fall back to the name when Path is empty.
function Get-OwnProcess([string]$exe) {
    $name = [IO.Path]::GetFileNameWithoutExtension($exe)
    Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path -eq $exe }
}

# SOCKS5 UDP ASSOCIATE + STUN request through a SOCKS server. Returns public IP seen over UDP, or $null.
function Test-SocksUdp([string]$ip, [int]$port) {
    $t = New-Object Net.Sockets.TcpClient
    try {
        $t.Connect($ip, $port); $st = $t.GetStream(); $st.ReadTimeout = 4000
        if ($User) {
            $st.Write([byte[]](5, 1, 2), 0, 3); $r = New-Object byte[] 2; [void]$st.Read($r, 0, 2)
            $u = [Text.Encoding]::ASCII.GetBytes($User); $pw = [Text.Encoding]::ASCII.GetBytes($Pass)
            $a = [byte[]](1, $u.Length) + $u + [byte[]]($pw.Length) + $pw; $st.Write($a, 0, $a.Length); [void]$st.Read($r, 0, 2)
        } else {
            $st.Write([byte[]](5, 1, 0), 0, 3); $r = New-Object byte[] 2; [void]$st.Read($r, 0, 2)
        }
        $st.Write([byte[]](5, 3, 0, 1, 0, 0, 0, 0, 0, 0), 0, 10)
        $r = New-Object byte[] 10; $n = $st.Read($r, 0, 10)
        if ($n -lt 10 -or $r[1] -ne 0) { return $null }
        $relay = "$($r[4]).$($r[5]).$($r[6]).$($r[7])"; $rport = $r[8] * 256 + $r[9]
        if ($relay -eq '0.0.0.0' -or $relay -eq '127.0.0.1') { $relay = $ip }
        $stunIp = ([Net.Dns]::GetHostAddresses('stun.l.google.com') | Where-Object AddressFamily -eq 'InterNetwork' | Select-Object -First 1).GetAddressBytes()
        $tid = New-Object byte[] 12; (New-Object Random).NextBytes($tid)
        $pkt = [byte[]](0, 0, 0, 1) + $stunIp + [byte[]](0x4B, 0x66) + [byte[]](0, 1, 0, 0, 0x21, 0x12, 0xA4, 0x42) + $tid
        $udp = New-Object Net.Sockets.UdpClient; $udp.Client.ReceiveTimeout = 3000
        try {
            for ($k = 0; $k -lt 3; $k++) {
                [void]$udp.Send($pkt, $pkt.Length, $relay, $rport)
                try { $ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0); $x = $udp.Receive([ref]$ep); return (Read-StunIp $x[10..($x.Length - 1)]) } catch {}
            }
        } finally { $udp.Close() }
        return $null
    } catch { return $null } finally { $t.Close() }
}

function Read-StunIp([byte[]]$r) {
    $i = 20
    while ($i + 4 -le $r.Length) {
        $type = $r[$i] * 256 + $r[$i + 1]; $len = $r[$i + 2] * 256 + $r[$i + 3]
        if ($type -eq 0x20) { return (($r[$i + 8] -bxor 0x21), ($r[$i + 9] -bxor 0x12), ($r[$i + 10] -bxor 0xA4), ($r[$i + 11] -bxor 0x42)) -join '.' }
        $i += 4 + $len + ((4 - $len % 4) % 4)
    }
    return 'cevap alindi'
}

# Plain UDP STUN (used when the VPN is running, to check the TUN path).
function Test-DirectStun {
    $s = New-Object Net.Sockets.UdpClient; $s.Client.ReceiveTimeout = 3000
    try {
        $ip = [Net.Dns]::GetHostAddresses('stun.l.google.com') | Where-Object AddressFamily -eq 'InterNetwork' | Select-Object -First 1
        $tid = New-Object byte[] 12; (New-Object Random).NextBytes($tid)
        $req = [byte[]](0, 1, 0, 0, 0x21, 0x12, 0xA4, 0x42) + $tid
        for ($k = 0; $k -lt 3; $k++) {
            [void]$s.Send($req, $req.Length, (New-Object Net.IPEndPoint($ip, 19302)))
            try { $ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0); return (Read-StunIp ($s.Receive([ref]$ep))) } catch {}
        }
        return $null
    } catch { return $null } finally { $s.Close() }
}
