using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneVPNDashboard
{
    internal class LogSource
    {
        public string Label;
        public string RelativePath;   // null = dashboard's own log

        public LogSource(string label, string relativePath)
        {
            Label = label;
            RelativePath = relativePath;
        }
    }

    /// <summary>Everything that differs between the Android (Exclave) and iPhone (Shadowrocket) installs.</summary>
    internal class PlatformProfile
    {
        public string Key;
        public string DisplayName;
        public string PhoneApp;
        public string DefaultFolderName;   // under %USERPROFILE%, as the installers use
        public string RepoSubdir;          // layout inside a git checkout
        public string[] SingboxProcesses;
        public string[] XrayProcesses;
        public string TunName;
        public string TunPrefix;           // local address prefix of the TUN adapter
        public string SingboxConfig;
        public string XrayConfig;
        public string SettingsFile;        // null when the platform has no editable settings
        public int DefaultClashPort;
        public LogSource[] Logs;

        public static readonly PlatformProfile Android = new PlatformProfile
        {
            Key = "android",
            DisplayName = "Android",
            PhoneApp = "Exclave",
            DefaultFolderName = "PhoneVPN-Android",
            RepoSubdir = Path.Combine("android", "app"),
            SingboxProcesses = new[] { "android-singbox" },
            XrayProcesses = new[] { "android-xray" },
            TunName = "android-tun",
            TunPrefix = "172.19.0.",
            SingboxConfig = "config.json",
            XrayConfig = Path.Combine("xray", "config.json"),
            SettingsFile = null,
            DefaultClashPort = 9095,
            Logs = new[]
            {
                new LogSource("Watcher", "watcher.log"),
                new LogSource("sing-box", "sing-box.log"),
                new LogSource("Xray", Path.Combine("xray", "xray-error.log")),
                new LogSource("Dashboard", null)
            }
        };

        public static readonly PlatformProfile Iphone = new PlatformProfile
        {
            Key = "iphone",
            DisplayName = "iPhone",
            PhoneApp = "Shadowrocket",
            DefaultFolderName = "PhoneVPN-iPhone",
            RepoSubdir = Path.Combine("iphone", "app"),
            SingboxProcesses = new[] { "phonevpn-singbox" },
            XrayProcesses = new[] { "phonevpn-xray" },
            TunName = "phonevpn-tun",
            TunPrefix = "172.19.1.",
            SingboxConfig = "singbox.json",
            XrayConfig = "xray-runtime.json",
            SettingsFile = "settings.json",
            DefaultClashPort = 9096,
            Logs = new[]
            {
                new LogSource("Watcher", "watcher.log"),
                new LogSource("sing-box", "singbox.log"),
                new LogSource("Xray", "xray-error.log"),
                new LogSource("Dashboard", null)
            }
        };

        public static readonly PlatformProfile[] All = { Android, Iphone };

        public static PlatformProfile ByKey(string key)
        {
            foreach (PlatformProfile p in All)
            {
                if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        public string Title
        {
            get { return DisplayName + " · " + PhoneApp; }
        }

        public bool IsOwnProcess(string name)
        {
            foreach (string n in SingboxProcesses) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string n in XrayProcesses) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal class PhoneLink
    {
        public string Ip;
        public int Port;
        public string AdapterId;
        public string AdapterName;
        public string Kind;          // USB, Wi-Fi, Bluetooth, Ethernet

        public string Describe()
        {
            if (string.IsNullOrEmpty(Ip)) return "Phone not found";
            string where = string.IsNullOrEmpty(AdapterName) ? "adapter not found" : Kind + " · " + AdapterName;
            return Ip + (Port > 0 ? ":" + Port : "") + "  (" + where + ")";
        }
    }

    internal static class PlatformLocator
    {
        /// <summary>A folder counts as an install when it has the start script and the watcher.</summary>
        public static bool IsInstallFolder(string dir)
        {
            try
            {
                return !string.IsNullOrEmpty(dir)
                    && File.Exists(Path.Combine(dir, "start.ps1"))
                    && File.Exists(Path.Combine(dir, "watcher.ps1"));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Folder of the copy installed on this PC (what the installers create), then a git checkout.</summary>
        public static string Detect(PlatformProfile p)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string installed = Path.Combine(home, p.DefaultFolderName);
            if (IsInstallFolder(installed)) return installed;

            string legacy = Path.Combine(installed, "app");
            if (IsInstallFolder(legacy)) return legacy;

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localCandidate = Path.Combine(localApp, p == PlatformProfile.Android ? "PhoneVPN-Android" : "PhoneVPN");
            if (IsInstallFolder(localCandidate)) return localCandidate;

            string cur = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(cur))
            {
                string candidate = Path.Combine(cur, p.RepoSubdir);
                if (IsInstallFolder(candidate)) return candidate;
                DirectoryInfo parent = Directory.GetParent(cur);
                if (parent == null) break;
                cur = parent.FullName;
            }
            return installed;
        }

        public static string Resolve(PlatformProfile p, string overrideFolder)
        {
            if (IsInstallFolder(overrideFolder)) return overrideFolder;
            return Detect(p);
        }

        public static bool IsRunning(PlatformProfile p)
        {
            foreach (string n in p.SingboxProcesses) if (AnyProcess(n)) return true;
            foreach (string n in p.XrayProcesses) if (AnyProcess(n)) return true;
            return false;
        }

        public static PlatformProfile DetectRunning()
        {
            foreach (PlatformProfile p in PlatformProfile.All)
            {
                if (IsRunning(p)) return p;
            }
            return null;
        }

        public static void CleanupGhostRoutes()
        {
            try
            {
                if (DetectRunning() == null)
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -NonInteractive -Command \"Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Where-Object { $_.InterfaceAlias -match 'tun|sing' -or $_.NextHop -match '^172\\.19\\.' } | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; Get-NetAdapter | Where-Object { $_.Name -match '^(phonevpn-tun|android-tun)$' -or $_.InterfaceDescription -match 'sing-tun' } | Disable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue; Clear-DnsClientCache -ErrorAction SilentlyContinue\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (Process p = Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit(3000);
                    }
                }
            }
            catch {}
        }

        public static bool AnyProcess(string name)
        {
            Process[] procs = null;
            try
            {
                procs = Process.GetProcessesByName(name);
                return procs.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (procs != null) foreach (Process p in procs) p.Dispose();
            }
        }

        /// <summary>Clash API port from the sing-box config, or 0 when the API is not enabled.</summary>
        public static int ReadClashPort(PlatformProfile p, string folder)
        {
            Dictionary<string, object> cfg = Json.ReadFile(Path.Combine(folder, p.SingboxConfig));
            string ctl = Json.Str(Json.Obj(Json.Obj(cfg, "experimental"), "clash_api"), "external_controller");
            if (string.IsNullOrEmpty(ctl)) return 0;
            int colon = ctl.LastIndexOf(':');
            int port;
            if (colon >= 0 && int.TryParse(ctl.Substring(colon + 1), out port)) return port;
            return 0;
        }

        public static PhoneLink FindPhone(PlatformProfile p, string folder)
        {
            PhoneLink link = new PhoneLink();

            // 1) The Xray config the watcher wrote points at the phone.
            Dictionary<string, object> xr = Json.ReadFile(Path.Combine(folder, p.XrayConfig));
            object[] outs = Json.Arr(xr, "outbounds");
            if (outs != null)
            {
                foreach (object o in outs)
                {
                    if (Json.Str(o, "tag") != "phone") continue;
                    object[] servers = Json.Arr(Json.Obj(o, "settings"), "servers");
                    if (servers != null && servers.Length > 0)
                    {
                        link.Ip = Json.Str(servers[0], "address");
                        link.Port = (int)Json.Long(servers[0], "port");
                    }
                }
            }

            // 2) Fallback: last "telefon bulundu" line of the watcher log, but only while
            //    it is still current - not after a later loss, tunnel shutdown or restart.
            if (string.IsNullOrEmpty(link.Ip))
            {
                string tail = ReadTail(Path.Combine(folder, "watcher.log"), 16384);
                MatchCollection ms = Regex.Matches(tail, @"telefon bulundu: (\d+\.\d+\.\d+\.\d+)(?::(\d+))?");
                MatchCollection lost = Regex.Matches(tail, @"bulunamadi|ulasilamiyor|tunel kapatildi|watcher basladi|artik bagli aglarda yok");
                int lastLost = lost.Count > 0 ? lost[lost.Count - 1].Index : -1;
                if (ms.Count > 0 && ms[ms.Count - 1].Index > lastLost)
                {
                    Match m = ms[ms.Count - 1];
                    link.Ip = m.Groups[1].Value;
                    if (m.Groups[2].Success) int.TryParse(m.Groups[2].Value, out link.Port);
                }
            }

            if (string.IsNullOrEmpty(link.Ip)) return link;

            IPAddress phoneIp;
            if (!IPAddress.TryParse(link.Ip, out phoneIp)) return link;

            NetworkInterface best = null;
            foreach (NetworkInterface ni in SafeInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (IsTunnelAdapter(ni)) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                {
                    if (g.Address.Equals(phoneIp)) { best = ni; break; }
                }
                if (best != null) break;

                foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    if (SameSubnet(u.Address, phoneIp, u.IPv4Mask)) { best = ni; break; }
                }
                if (best != null) break;
            }

            if (best != null)
            {
                link.AdapterId = best.Id;
                link.AdapterName = best.Name;
                link.Kind = KindOf(best);
            }
            return link;
        }

        public static NetworkInterface[] SafeInterfaces()
        {
            try { return NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return new NetworkInterface[0]; }
        }

        public static bool IsTunnelAdapter(NetworkInterface ni)
        {
            string n = (ni.Name + " " + ni.Description).ToLowerInvariant();
            return n.Contains("tun") || n.Contains("wintun") || n.Contains("sing-") || n.Contains("clash") || n.Contains("v2ray");
        }

        private static string KindOf(NetworkInterface ni)
        {
            string d = (ni.Description ?? "").ToLowerInvariant();
            if (d.Contains("ndis") || d.Contains("usb") || d.Contains("tether")) return "USB";
            if (d.Contains("bluetooth")) return "Bluetooth";
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || d.Contains("wi-fi") || d.Contains("wireless")) return "Wi-Fi";
            return "Ethernet";
        }

        private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
        {
            byte[] x = a.GetAddressBytes(), y = b.GetAddressBytes(), m = mask.GetAddressBytes();
            if (x.Length != y.Length || x.Length != m.Length) return false;
            for (int i = 0; i < x.Length; i++)
            {
                if ((x[i] & m[i]) != (y[i] & m[i])) return false;
            }
            return true;
        }

        public static string ReadTail(string path, int maxBytes)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - maxBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - start];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
