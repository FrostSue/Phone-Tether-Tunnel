# phone-tether-tunnel

Share the VLESS tunnel running on your **phone** with a **Windows PC**, so that *all* of the
PC's traffic — web, games, and **Discord voice (UDP)** — leaves through the phone's VPN
instead of as plain hotspot traffic.

One repository, two independent systems:

| Folder | Phone app | How the PC reaches the tunnel |
|---|---|---|
| [`android/`](android/) | [Exclave](https://github.com/dyhkwong/Exclave) (Android) | Exclave's LAN SOCKS5 port (`2080`) — TCP **and** UDP |
| [`iphone/`](iphone/) | [Shadowrocket](https://apps.apple.com/app/shadowrocket/id932747118) (iOS) | Shadowrocket *Proxy Share* (`1082`) for TCP; UDP is carried inside a VLESS connection that is dialed **through** the share |

Both work over the phone's **Wi-Fi hotspot** or **USB tethering**, pick the phone up
automatically, follow IP/port changes, and never fall back to sending traffic directly.

---

## How it works

```
Android:  apps ─► sing-box (TUN) ─► Xray (127.0.0.1:17880) ─► phone SOCKS5 :2080 ─► Exclave ─► VLESS server

iPhone:   TCP ─► sing-box (TUN) ─► Xray (127.0.0.1:17890) ─────────────────────► Proxy Share :1082 ─► Shadowrocket ─► server
          UDP ─► sing-box (TUN) ─► Xray ─► VLESS (xudp, inside TCP, dialerProxy) ─► Proxy Share :1082 ─► server
```

* **sing-box** creates a TUN adapter and captures every connection (TCP, UDP, DNS).
* **Xray** sits in the middle because Exclave answers SOCKS5 `UDP ASSOCIATE` with relay
  address `0.0.0.0`. sing-box's SOCKS client uses that address as-is and its UDP never
  arrives; Xray replaces an unspecified address with the server address.
* **Shadowrocket's Proxy Share accepts `UDP ASSOCIATE` but forwards no datagrams**
  (verified with DNS and STUN probes). The iPhone build therefore opens its own VLESS
  connection to your server *through* the share (`sockopt.dialerProxy`) and routes UDP into
  it, so the share only ever carries TCP.
* Xray 26 removed `allowInsecure`. For links that carry `allowInsecure=1` (self-signed server
  certificate), the watcher fetches the server certificate through the phone and pins its
  SHA-256 (`pinnedPeerCertSha256`), refreshing it when the phone reconnects.
* **Leak cleanup:** TCP connections opened during a restart gap stay bound to the physical
  adapter even after the TUN is back, silently using mobile data. Every 5 seconds the watcher
  closes such connections (`SetTcpEntry` → `DELETE_TCB`); apps reconnect through the tunnel.
* The two systems use different executable names (`android-*`, `phonevpn-*`), TUN names and
  local ports, and each one stops the other (plus v2rayN, Proxifier, other sing-box/Xray
  cores) when it starts.

## Requirements

* Windows 10/11 (x64), administrator rights (the TUN adapter needs them)
* Android: Exclave connected to your server, **Settings → Allow Connections from the LAN** on
* iPhone: Shadowrocket connected, **Settings → Proxy → Enable Share** on, server **UDP Relay:
  xudp**, **Tunnel → Include All Networks** off, **HTTPS Decrypt** off.
  For USB on Windows install *Apple Devices* (Microsoft Store) or iTunes.
* iPhone + Discord voice: your server's `vless://` link (TLS / REALITY / WS / gRPC / XHTTP).
  Plain VLESS without TLS to a bare IP is refused by Xray 26.

## Phone setup

Do this once on the phone before installing on the PC.

### Android — Exclave

1. Import your server into Exclave and connect (the VPN key icon appears in the status bar).
2. Open **Settings** and, under the inbound / proxy section, turn on
   **Allow Connections from the LAN**. This makes Exclave's SOCKS5 port reachable from the
   hotspot and USB network instead of only from the phone itself.
3. Check that the **SOCKS5 port is `2080`** (Exclave's default). The PC side looks for exactly
   this port; if you change it, change `$Port` in `android/app/watcher.ps1` too.
4. **Disconnect and reconnect** in Exclave so the LAN setting takes effect.
5. In Android **Settings → Apps → Exclave → Battery**, choose **Unrestricted**, otherwise the
   tunnel may stop when the screen turns off.
6. If you use per-app proxy / bypass in Exclave, do not exclude Exclave itself.
7. Share the connection with the PC:
   * **Wi-Fi:** Settings → Network & internet → Hotspot & tethering → **Wi-Fi hotspot** on.
   * **USB:** plug in the cable, then **USB tethering** on (same menu). No extra driver needed.
   * Some phones (Samsung, Xiaomi) have a hotspot "client isolation" option — keep it off.

Check from the PC after connecting: `android\app\test.ps1` (or the **Android VPN Test**
shortcut after installing) must report `Telefon bulundu: <gateway>:2080`.

### iOS — Shadowrocket

1. Import your server into Shadowrocket and connect.
2. Open the server's details (the **ⓘ** next to it) and set **UDP Relay** to **`xudp`**.
   If voice still fails later, try `packetaddr`, then `on`.
3. **Settings → UDP:** **Enable Relay** on, **Disable STUN** off (Discord needs STUN),
   **Allow Ports** empty.
4. **Settings → Proxy:** **Enable Share** on. Note the port shown (usually `1082`); the PC
   side probes the ports listed in `iphone/app/settings.json`, so add yours there if it differs.
5. **Settings → Proxy → HTTPS Decrypt:** off (it intercepts TLS and is not needed).
6. **Settings → Tunnel → Include All Networks:** off (it pulls the hotspot's local network,
   including the PC's connection to the share, into the tunnel).
7. Copy your server link for the PC: swipe the server left (or long-press) →
   **Share → Copy**. You will paste this `vless://…` line into `vless.txt` during install.
   Keep it private; it is the key to your server.
8. Share the connection with the PC:
   * **Wi-Fi:** Settings → **Personal Hotspot** → Allow Others to Join. The phone is
     usually `172.20.10.1`.
   * **USB:** install **Apple Devices** (Microsoft Store) or iTunes on Windows, plug in,
     tap **Trust**, keep Personal Hotspot on.
9. Leave Shadowrocket running in the background.

Why the link is needed: Shadowrocket's Proxy Share carries TCP but drops UDP. With the link,
the PC opens its own VLESS connection to your server *through* the share and sends UDP
(Discord voice) inside it. Without the link only TCP (web, downloads, Discord text) works.

## Install

1. Connect the PC to the phone (hotspot or USB) with the phone's VPN already on.
2. Run **`android\install.bat`** or **`iphone\install.bat`**.
   * Files go to `%USERPROFILE%\PhoneVPN-Android` or `%USERPROFILE%\PhoneVPN-iPhone`.
   * sing-box 1.14.4 and Xray-core 26.10.31 are downloaded from their official GitHub
     releases **through the phone's proxy** when it is reachable, and verified against pinned
     SHA-256 hashes. To install offline, drop the two release zips into `android\cores\` or
     `iphone\cores\` first.
   * Desktop shortcuts: **Android VPN Baslat / Durdur / Test** or **iPhone VPN Baslat / Durdur / Test**.
3. iPhone only: paste your `vless://` link into `vless.txt` (Notepad opens it) and save.
4. Start with **… Baslat**, check with **… Test**, stop with **… Durdur**.

## Desktop Dashboard (GUI)

Run **`Dashboard.bat`**. It builds `PhoneVPNDashboard.exe` from `ui\` with the .NET Framework
compiler that ships with Windows (again whenever a file in `ui\` changes) and starts it.

* **Finds the running copy:** picks the platform whose tunnel is running and reads that install
  folder (`%USERPROFILE%\PhoneVPN-Android` / `PhoneVPN-iPhone`); another folder can be chosen in
  Settings.
* **Overview:** tunnel state, phone address and link (USB / Wi-Fi), live speed, a 60-second
  throughput chart, top apps of the session and the last 7 days.
* **Data usage:** session, today and all-time totals, plus a **usage board** — tick apps in the
  list to compare their download / upload / share, for the session or all time.
* **Connections:** live connections per app, with route (tunnel / direct), destination and, with
  exact tracking, bytes, speed and age (TCP and UDP).
* **Logs:** watcher, sing-box and Xray logs, followed live.
* **Settings:** install folder, exact per-app tracking, sing-box log level, iPhone ports / VLESS
  mode / interval / link.
* Start, Stop (with confirmation) and Restart run the platform's scripts as administrator;
  Diagnose runs `test.ps1` and shows its output in the Dashboard log.

How usage is measured:

* **Totals** come from the network adapter that reaches the phone (found from the Xray config the
  watcher writes), so they are the mobile data actually used, split into download and upload. The
  TUN adapter is not used for totals: sing-tun counts both directions in both of its counters.
* **Per app** is exact when sing-box's Clash API is enabled (`experimental.clash_api` on
  `127.0.0.1:9095` / `9096` and `route.find_process`, included in the configs in this repository):
  the dashboard reads each connection's byte counters. Without it the total is split by open
  tunnel connections per app, which is only a rough guide — Windows process I/O counters do not
  include socket traffic, so they cannot be used for this. Settings → *Enable exact per-app
  tracking* adds the API to an existing install (a `.bak` copy is kept; restart the tunnel after).
* Statistics are saved to `dashboard-stats.json` in the install folder (daily totals for about four
  months, all-time totals per app). Dashboard preferences live in `%LOCALAPPDATA%\PhoneTetherTunnel`.

The icon is generated by `ui\make-icon.ps1`.

Turkish step-by-step guides: [`android/OKU-BENI.txt`](android/OKU-BENI.txt),
[`iphone/OKU-BENI.txt`](iphone/OKU-BENI.txt).

## Configuration

| File | Purpose |
|---|---|
| `iphone/app/settings.json` | Proxy Share ports to probe, optional SOCKS user/password, `vlessMode` (`udp` = only UDP via VLESS, `all` = everything) |
| `iphone/app/vless.txt.example` | Template; the installer copies it to `vless.txt` (git-ignored, never committed) |
| `android/app/config.json`, `iphone/app/singbox.json` | sing-box TUN / routing; local Clash API for the dashboard's per-app usage |
| `*/app/watcher.ps1` | Phone discovery, Xray config generation, health checks, leak cleanup |

Logs: `watcher.log`, `singbox.log` / `sing-box.log`, `xray-error.log` in the install folder.

## Sources and references

Software

* sing-box — https://github.com/SagerNet/sing-box · docs https://sing-box.sagernet.org/ · release used: https://github.com/SagerNet/sing-box/releases/tag/v1.14.4
* Xray-core — https://github.com/XTLS/Xray-core · docs https://xtls.github.io/ · release used: https://github.com/XTLS/Xray-core/releases/tag/v26.10.31
* Exclave — https://github.com/dyhkwong/Exclave
* Shadowrocket — https://apps.apple.com/app/shadowrocket/id932747118
* Wintun (TUN driver used by sing-box) — https://www.wintun.net/
* Proxifier (first approach, later replaced) — https://www.proxifier.com/

Source code that explained the UDP problems

* sing SOCKS5 client, UDP associate uses the reply address directly — https://github.com/SagerNet/sing/blob/main/protocol/socks/client.go
* Xray SOCKS client, replaces `0.0.0.0` with the server address — https://github.com/XTLS/Xray-core/blob/main/proxy/socks/client.go

Specifications and APIs

* RFC 1928 — SOCKS Protocol Version 5 — https://www.rfc-editor.org/rfc/rfc1928
* RFC 8489 — STUN (used for UDP checks) — https://www.rfc-editor.org/rfc/rfc8489
* `SetTcpEntry` (leak cleanup) — https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-settcpentry

Articles consulted while researching Shadowrocket sharing / UDP

* Proxidize — What is Shadowrocket — https://proxidize.com/blog/what-is-shadowrocket/
* Proxidize — Test UDP over SOCKS — https://proxidize.com/blog/test-udp-over-socks
* Shadowrocket LAN sharing guide (zh) — https://shadowrocket-ios.com/Shadowrocket%E5%B1%80%E5%9F%9F%E7%BD%91%E5%85%B1%E4%BA%AB%E6%95%99%E7%A8%8B.html
* Shadowrocket LAN gateway guide (zh) — https://clashnf.com/Shadowrocket%E5%B1%80%E5%9F%9F%E7%BD%91%E5%85%B1%E4%BA%AB%E9%85%8D%E7%BD%AE%EF%BC%8C%E8%AE%A9iPhone%E6%88%90%E4%B8%BA%E5%B1%80%E5%9F%9F%E7%BD%91%E7%BD%91%E5%85%B3%E7%9A%84%E5%AE%9E%E6%93%8D%E6%AD%A5%E9%AA%A4.html
* Karing — Port Sharing — https://karing.app/en/tutorial/lan
* Hiddify for iOS — https://apps.apple.com/us/app/hiddify-proxy-vpn-client/id6596777532
* Discord Drover wiki — https://github.com/hdrover/discord-drover/wiki
* FreeBSD forum, Xray client with xudp — https://forums.freebsd.org/threads/error-trying-to-setup-security-xray-core-client.103138/

## License

The scripts and configuration in this repository are released under the [MIT License](LICENSE).

sing-box (GPL-3.0-or-later) and Xray-core (MPL-2.0) are **not** included in the repository;
the installers download the official, unmodified release binaries, which remain under their
own licenses. Make sure your use of this setup complies with your mobile operator's terms.
