using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PhoneVPNDashboard
{
    internal class ConnInfo
    {
        public string Key;
        public string App;
        public string Network;       // TCP / UDP
        public string Destination;
        public string Route;         // Tunnel, Direct, Phone link, ...
        public bool Tunnel;
        public long Down;            // cumulative bytes (Clash API only)
        public long Up;
        public DateTime Start;       // Clash API only
        public string State;         // TCP table only
    }

    /// <summary>One measurement, taken on the collector thread and applied on the UI thread.</summary>
    internal class Snapshot
    {
        public DateTime Time;
        public PlatformProfile Profile;
        public string Folder;
        public bool SingboxRunning;
        public bool XrayRunning;
        public PlatformProfile OtherRunning;

        public PhoneLink Phone;
        public bool UplinkOk;
        public string UplinkId;
        public long UplinkRx;
        public long UplinkTx;

        public bool TunOk;
        public long TunRx;

        public int ClashPort;
        public bool ClashOk;
        public long ClashDownTotal;
        public long ClashUpTotal;

        public List<ConnInfo> Connections = new List<ConnInfo>();
        public Dictionary<string, int> AppSockets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Polls processes, adapters, the Clash API and the TCP table once a second off the UI thread.</summary>
    internal class Collector
    {
        private static readonly HashSet<string> IgnoredApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "android-singbox", "android-xray", "phonevpn-singbox", "phonevpn-xray"
        };

        private readonly object gate = new object();
        private PlatformProfile profile;
        private string folder;
        private bool resetRequested;

        private Thread thread;
        private volatile bool stopping;
        private int tick;

        private PhoneLink phone;
        private int clashPort;
        private int clashRetryAt;
        private readonly Dictionary<int, string> pidNames = new Dictionary<int, string>();

        public event Action<Snapshot> SnapshotReady;
        public event Action<string> Error;

        public void Configure(PlatformProfile p, string installFolder)
        {
            lock (gate)
            {
                profile = p;
                folder = installFolder;
                resetRequested = true;
            }
        }

        /// <summary>Forces the next tick to re-read the phone address and the Clash API port.</summary>
        public void Refresh()
        {
            lock (gate) resetRequested = true;
        }

        public void Start()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "collector", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
        }

        private void Loop()
        {
            while (!stopping)
            {
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    Snapshot s = Collect();
                    Action<Snapshot> h = SnapshotReady;
                    if (s != null && h != null) h(s);
                }
                catch (Exception ex)
                {
                    Action<string> e = Error;
                    if (e != null) e(ex.Message);
                }
                int wait = 1000 - (int)sw.ElapsedMilliseconds;
                Thread.Sleep(Math.Max(150, wait));
            }
        }

        private Snapshot Collect()
        {
            PlatformProfile p;
            string dir;
            bool reset;
            lock (gate)
            {
                p = profile;
                dir = folder;
                reset = resetRequested;
                resetRequested = false;
            }
            if (p == null) return null;

            if (reset)
            {
                phone = null;
                clashPort = -1;
                clashRetryAt = 0;
                tick = 0;
            }
            tick++;

            Snapshot s = new Snapshot { Time = DateTime.Now, Profile = p, Folder = dir };
            foreach (string n in p.SingboxProcesses) if (PlatformLocator.AnyProcess(n)) s.SingboxRunning = true;
            foreach (string n in p.XrayProcesses) if (PlatformLocator.AnyProcess(n)) s.XrayRunning = true;
            if (!s.SingboxRunning && !s.XrayRunning)
            {
                foreach (PlatformProfile other in PlatformProfile.All)
                {
                    if (other != p && PlatformLocator.IsRunning(other)) s.OtherRunning = other;
                }
            }

            // Phone address and the adapter that reaches it (re-resolved every 5 s; the watcher may move it).
            if (phone == null || tick % 5 == 1) phone = PlatformLocator.FindPhone(p, dir);
            s.Phone = phone;

            NetworkInterface[] nics = PlatformLocator.SafeInterfaces();
            foreach (NetworkInterface ni in nics)
            {
                try
                {
                    if (phone != null && phone.AdapterId != null && ni.Id == phone.AdapterId && ni.OperationalStatus == OperationalStatus.Up)
                    {
                        IPInterfaceStatistics st = ni.GetIPStatistics();
                        s.UplinkOk = true;
                        s.UplinkId = ni.Id;
                        s.UplinkRx = st.BytesReceived;
                        s.UplinkTx = st.BytesSent;
                    }
                    if (string.Equals(ni.Name, p.TunName, StringComparison.OrdinalIgnoreCase))
                    {
                        s.TunOk = true;
                        s.TunRx = ni.GetIPStatistics().BytesReceived;
                    }
                }
                catch { }
            }

            // Exact per-connection numbers from sing-box's Clash API, when the config enables it.
            if (clashPort < 0 || tick % 10 == 0) clashPort = PlatformLocator.ReadClashPort(p, dir);
            s.ClashPort = clashPort;
            if (clashPort > 0 && s.SingboxRunning && tick >= clashRetryAt)
            {
                if (!ReadClash(s, p)) clashRetryAt = tick + 5;
            }

            if (!s.ClashOk) ReadTcpTable(s, p);

            if (tick % 60 == 0) pidNames.Clear();
            return s;
        }

        private bool ReadClash(Snapshot s, PlatformProfile p)
        {
            string body;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + s.ClashPort + "/connections");
                req.Proxy = null;
                req.Timeout = 700;
                req.ReadWriteTimeout = 700;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    body = r.ReadToEnd();
                }
            }
            catch
            {
                return false;
            }

            Dictionary<string, object> root = Json.ParseObject(body);
            if (root == null) return false;
            s.ClashOk = true;
            s.ClashDownTotal = Json.Long(root, "downloadTotal");
            s.ClashUpTotal = Json.Long(root, "uploadTotal");

            object[] conns = Json.Arr(root, "connections");
            if (conns == null) return true;
            foreach (object c in conns)
            {
                Dictionary<string, object> md = Json.Obj(c, "metadata");
                string path = Json.Str(md, "processPath");
                string app = string.IsNullOrEmpty(path) ? "Unknown" : Path.GetFileNameWithoutExtension(path);
                if (IgnoredApps.Contains(app)) continue;

                string host = Json.Str(md, "host");
                string ip = Json.Str(md, "destinationIP");
                string port = Json.Str(md, "destinationPort");
                string dest = !string.IsNullOrEmpty(host) ? host : ip;
                if (!string.IsNullOrEmpty(port)) dest += ":" + port;

                object[] chains = Json.Arr(c, "chains");
                string outbound = chains != null && chains.Length > 0 ? Convert.ToString(chains[0], CultureInfo.InvariantCulture) : "";
                bool tunnel = !string.Equals(outbound, "direct", StringComparison.OrdinalIgnoreCase);

                ConnInfo ci = new ConnInfo
                {
                    Key = Json.Str(c, "id"),
                    App = app,
                    Network = (Json.Str(md, "network") ?? "").ToUpperInvariant(),
                    Destination = dest,
                    Route = tunnel ? "Tunnel" : "Direct",
                    Tunnel = tunnel,
                    Down = Json.Long(c, "download"),
                    Up = Json.Long(c, "upload"),
                    Start = ParseTime(Json.Str(c, "start"))
                };
                s.Connections.Add(ci);
                if (tunnel)
                {
                    int n;
                    s.AppSockets.TryGetValue(app, out n);
                    s.AppSockets[app] = n + 1;
                }
            }
            return true;
        }

        private void ReadTcpTable(Snapshot s, PlatformProfile p)
        {
            List<TcpRow> rows;
            try { rows = Native.GetTcpConnections(); }
            catch { return; }

            string phoneIp = s.Phone != null ? s.Phone.Ip : null;
            foreach (TcpRow r in rows)
            {
                if (r.State == 2) continue;                                     // listening
                if (IPAddress.IsLoopback(r.RemoteAddress) || IPAddress.IsLoopback(r.LocalAddress)) continue;
                if (r.RemoteAddress.Equals(IPAddress.Any) || r.RemotePort == 0) continue;

                string app = NameOf(r.Pid);
                if (IgnoredApps.Contains(app)) continue;

                string local = r.LocalAddress.ToString();
                string remote = r.RemoteAddress.ToString();
                bool tunnel = local.StartsWith(p.TunPrefix, StringComparison.Ordinal);
                string route = tunnel ? "Tunnel" : (remote == phoneIp ? "Phone link" : "Direct");

                s.Connections.Add(new ConnInfo
                {
                    Key = r.Pid + "|" + local + ":" + r.LocalPort + "|" + remote + ":" + r.RemotePort,
                    App = app,
                    Network = "TCP",
                    Destination = remote + ":" + r.RemotePort,
                    Route = route,
                    Tunnel = tunnel,
                    State = Native.TcpStateName(r.State)
                });

                // Note: process I/O counters cannot weight this - Winsock traffic does not show up in
                // them (a 15 MB curl download reads 0 bytes), only disk I/O does. Open sockets are the
                // only per-app signal available without admin rights or the Clash API.
                if (tunnel && r.State == 5)
                {
                    int n;
                    s.AppSockets.TryGetValue(app, out n);
                    s.AppSockets[app] = n + 1;
                }
            }
        }

        private string NameOf(int pid)
        {
            if (pid == 0) return "Idle";
            if (pid == 4) return "System";
            string name;
            if (pidNames.TryGetValue(pid, out name)) return name;
            try
            {
                using (Process proc = Process.GetProcessById(pid)) name = proc.ProcessName;
            }
            catch
            {
                name = "PID " + pid;
            }
            pidNames[pid] = name;
            return name;
        }

        private static DateTime ParseTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
            // Go writes up to 9 fractional digits; .NET accepts 7.
            s = Regex.Replace(s, @"(\.\d{7})\d+", "$1");
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto)) return dto.LocalDateTime;
            return DateTime.MinValue;
        }
    }
}
