# Android (Exclave) VPN installer.
# Copies app\ to the install folder, downloads the pinned sing-box / Xray releases
# (through the phone's proxy when the phone is already connected, so the download
# does not count as plain hotspot data), verifies SHA256 and creates shortcuts.
param([string]$Dest = (Join-Path $env:USERPROFILE 'PhoneVPN-Android'), [switch]$NoShortcuts, [switch]$NoTest)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'app'

$Cores = @(
    @{ Name = 'sing-box'; Url = 'https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip'
       ZipSha = '99185D75D314A07DBBA4A130C8AD986CBEB629A07927975636B341CA61F991DF'; Inner = 'sing-box.exe'
       ExeSha = '8F30682EE198855440D2A7FE070D5D3653793B2D01BB8C85EEA88F93BBE7C8B9'; Target = 'android-singbox.exe' },
    @{ Name = 'Xray-core'; Url = 'https://github.com/XTLS/Xray-core/releases/download/v26.10.31/Xray-windows-64.zip'
       ZipSha = '2E04A9995A6F2AD1B28C0BB25C708ECB2695E43A1C9C7889C4B6AC2B48CF7A6A'; Inner = 'xray.exe'
       ExeSha = 'D5B558191CCCB1E92A5772B68B8CAB67717B26F634B0F15153215F65AE982A7E'; Target = 'xray\android-xray.exe' }
)
$PhonePorts = @(2080)

function Find-PhoneProxy {
    $up = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up' | ForEach-Object { $_.ifIndex })
    $gws = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Where-Object { $up -contains $_.ifIndex -and $_.NextHop -ne '0.0.0.0' -and $_.InterfaceAlias -notmatch 'tun|sing|clash|v2ray' }
    foreach ($g in $gws) { foreach ($p in $PhonePorts) {
        $c = New-Object Net.Sockets.TcpClient
        try {
            $r = $c.BeginConnect($g.NextHop, $p, $null, $null)
            if ($r.AsyncWaitHandle.WaitOne(700) -and $c.Connected) {
                $s = $c.GetStream(); $s.ReadTimeout = 1500; $s.Write([byte[]](5, 1, 0), 0, 3)
                $b = New-Object byte[] 2
                if ($s.Read($b, 0, 2) -eq 2 -and $b[0] -eq 5 -and $b[1] -eq 0) { return "$($g.NextHop):$p" }
            }
        } catch {} finally { $c.Close() }
    } }
    return $null
}

function Install-Cores([string]$dir) {
    $need = $Cores | Where-Object { $t = Join-Path $dir $_.Target; -not ((Test-Path $t) -and (Get-FileHash $t -Algorithm SHA256).Hash -eq $_.ExeSha) }
    if (-not $need) { Write-Host 'Programlar zaten kurulu ve dogrulandi.'; return }
    $proxy = Find-PhoneProxy
    if ($proxy) { Write-Host "Indirme telefonun tuneli uzerinden yapilacak ($proxy)." }
    else { Write-Host 'UYARI: Telefon bulunamadi, indirme dogrudan yapilacak (~50 MB).' -ForegroundColor Yellow }
    $tmp = Join-Path $env:TEMP ("phonevpn-dl-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        foreach ($c in $need) {
            $zip = Join-Path $tmp ([IO.Path]::GetFileName($c.Url))
            $local = Join-Path $PSScriptRoot ('cores\' + [IO.Path]::GetFileName($c.Url))
            if (Test-Path $local) { Copy-Item $local $zip }
            else {
                Write-Host "$($c.Name) indiriliyor..."
                $cargs = @('-L', '-sS', '--fail', '--retry', '3', '--ssl-revoke-best-effort', '-o', $zip)
                if ($proxy) { $cargs += @('--socks5-hostname', $proxy) }
                & curl.exe @cargs $c.Url
                if ($LASTEXITCODE -ne 0) { throw "$($c.Name) indirilemedi (curl kodu $LASTEXITCODE)." }
            }
            if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $c.ZipSha) { throw "$($c.Name): SHA256 uyusmuyor, dosya reddedildi." }
            $x = Join-Path $tmp ($c.Name + '-x'); Expand-Archive $zip -DestinationPath $x -Force
            $exe = Get-ChildItem $x -Recurse -Filter $c.Inner | Select-Object -First 1
            if ((Get-FileHash $exe.FullName -Algorithm SHA256).Hash -ne $c.ExeSha) { throw "$($c.Name): exe SHA256 uyusmuyor." }
            $t = Join-Path $dir $c.Target
            New-Item -ItemType Directory -Force (Split-Path $t) | Out-Null
            Copy-Item $exe.FullName $t -Force
            Write-Host "$($c.Name) kuruldu ve dogrulandi."
        }
    } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host '=== Android VPN kurulumu ===' -ForegroundColor Cyan
$oldStop = Join-Path $Dest 'stop.ps1'
if (Test-Path (Join-Path $Dest 'watcher.pid')) {
    Write-Host 'Calisan eski kurulum durduruluyor (yonetici izni istenebilir)...'
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$oldStop`"" -Verb RunAs -Wait
}
New-Item -ItemType Directory -Force (Join-Path $Dest 'xray') | Out-Null
Get-ChildItem $src | Where-Object { $_.Name -ne 'xray' } | Copy-Item -Destination $Dest -Recurse -Force
Write-Host "Dosyalar kopyalandi: $Dest"
Install-Cores $Dest
Get-ChildItem $Dest -Recurse -File | Unblock-File

if (-not $NoShortcuts) {
    $desk = [Environment]::GetFolderPath('Desktop'); $sh = New-Object -ComObject WScript.Shell
    foreach ($l in @(@('Android VPN Baslat', 'VPN-Baslat.bat'), @('Android VPN Durdur', 'VPN-Durdur.bat'), @('Android VPN Test', 'VPN-Test.bat'))) {
        $k = $sh.CreateShortcut((Join-Path $desk "$($l[0]).lnk"))
        $k.TargetPath = Join-Path $Dest $l[1]; $k.WorkingDirectory = $Dest
        $k.IconLocation = (Join-Path $Dest 'android-singbox.exe') + ',0'; $k.Save()
    }
    Write-Host 'Masaustu kisayollari: Android VPN Baslat / Durdur / Test'
}
Write-Host ''
Write-Host 'Kurulum tamam.' -ForegroundColor Green
if (-not $NoTest) { & (Join-Path $Dest 'test.ps1') }
