using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PhoneVPNDashboard
{
    internal class AppUsage
    {
        public long SessionDown;
        public long SessionUp;
        public long LifetimeDown;
        public long LifetimeUp;
    }

    /// <summary>
    /// Turns snapshots into session / daily / lifetime totals and a per-app split, and persists them.
    ///
    /// Totals come from the adapter that reaches the phone (USB / Wi-Fi): that is the mobile data
    /// actually used, with the right direction. The TUN adapter is not used for totals because
    /// sing-tun counts both directions in both counters.
    ///
    /// The per-app split is exact when sing-box's Clash API is enabled (per-connection byte counts),
    /// otherwise it is a rough estimate weighted by each app's open tunnel connections.
    /// </summary>
    internal class UsageTracker
    {
        public const string OtherApp = "Other";
        private const int HistoryLength = 60;

        public readonly PlatformProfile Profile;
        public readonly string StatsPath;

        public long SessionDown, SessionUp, LifetimeDown, LifetimeUp;
        public DateTime SessionStart = DateTime.Now;
        public DateTime TrackingSince = DateTime.Now;
        public readonly Dictionary<string, AppUsage> Apps = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);
        public readonly SortedDictionary<string, long[]> Daily = new SortedDictionary<string, long[]>(StringComparer.Ordinal);

        public double SpeedDown, SpeedUp;
        public readonly List<double> HistoryDown = new List<double>();
        public readonly List<double> HistoryUp = new List<double>();
        public readonly Dictionary<string, double> ConnSpeed = new Dictionary<string, double>();

        public string Source = "Waiting for data";
        public bool ExactApps;

        private DateTime lastTime = DateTime.MinValue;
        private string lastUplinkId;
        private long lastRx = -1, lastTx = -1;
        private long lastClashDown = -1, lastClashUp = -1;
        private long lastTun = -1;
        private Dictionary<string, ConnState> lastConn = new Dictionary<string, ConnState>();
        private long clashSeenDown, clashSeenUp, attributedDown, attributedUp;
        private int idleTicks;

        private class ConnState
        {
            public long Down, Up, LastDelta;
            public string App;
        }
        private int ticksSinceSave;
        private bool dirty;

        public UsageTracker(PlatformProfile profile, string installFolder)
        {
            Profile = profile;
            StatsPath = ChooseStatsPath(profile, installFolder);
            for (int i = 0; i < HistoryLength; i++)
            {
                HistoryDown.Add(0);
                HistoryUp.Add(0);
            }
            Load();
        }

        /// <summary>Stats live in the install folder of the running copy; %LOCALAPPDATA% when it is not writable.</summary>
        private static string ChooseStatsPath(PlatformProfile p, string folder)
        {
            try
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    string probe = Path.Combine(folder, ".dashboard-write-test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return Path.Combine(folder, "dashboard-stats.json");
                }
            }
            catch { }
            Directory.CreateDirectory(Prefs.DataDir);
            return Path.Combine(Prefs.DataDir, "stats-" + p.Key + ".json");
        }

        public static string Today
        {
            get { return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        }

        public long[] TodayTotals
        {
            get
            {
                long[] v;
                return Daily.TryGetValue(Today, out v) ? v : new long[2];
            }
        }

        public void Apply(Snapshot s)
        {
            double secs = lastTime == DateTime.MinValue ? 1.0 : Math.Max(0.2, (s.Time - lastTime).TotalSeconds);
            lastTime = s.Time;
            bool active = s.SingboxRunning;

            // --- totals -------------------------------------------------------------------
            long down = 0, up = 0;

            long clashDown = 0, clashUp = 0;
            bool clashBaseline = s.ClashOk && lastClashDown < 0;
            if (s.ClashOk)
            {
                if (lastClashDown >= 0 && s.ClashDownTotal >= lastClashDown && s.ClashUpTotal >= lastClashUp)
                {
                    clashDown = s.ClashDownTotal - lastClashDown;
                    clashUp = s.ClashUpTotal - lastClashUp;
                }
                lastClashDown = s.ClashDownTotal;
                lastClashUp = s.ClashUpTotal;
            }
            else
            {
                lastClashDown = lastClashUp = -1;
            }

            long tunDelta = 0;
            if (s.TunOk)
            {
                if (lastTun >= 0 && s.TunRx >= lastTun) tunDelta = s.TunRx - lastTun;
                lastTun = s.TunRx;
            }
            else
            {
                lastTun = -1;
            }

            if (s.UplinkOk)
            {
                if (s.UplinkId == lastUplinkId && lastRx >= 0 && s.UplinkRx >= lastRx && s.UplinkTx >= lastTx)
                {
                    down = s.UplinkRx - lastRx;
                    up = s.UplinkTx - lastTx;
                }
                lastUplinkId = s.UplinkId;
                lastRx = s.UplinkRx;
                lastTx = s.UplinkTx;
                Source = s.Phone.Kind + " link · " + s.Phone.AdapterName;
            }
            else
            {
                lastUplinkId = null;
                lastRx = lastTx = -1;
                if (s.ClashOk)
                {
                    down = clashDown;
                    up = clashUp;
                    Source = "sing-box totals (phone adapter not found)";
                }
                else if (s.TunOk)
                {
                    down = tunDelta;   // both directions combined; no way to split them here
                    Source = "Tunnel adapter, combined (phone adapter not found)";
                }
                else
                {
                    Source = active ? "No counters available" : "Tunnel stopped";
                }
            }

            if (!active)
            {
                down = up = 0;
                if (!s.UplinkOk) Source = "Tunnel stopped";
            }

            // --- per-app split -------------------------------------------------------------
            Dictionary<string, long[]> appDelta = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            ConnSpeed.Clear();
            ExactApps = s.ClashOk;

            if (s.ClashOk)
            {
                Dictionary<string, ConnState> seen = new Dictionary<string, ConnState>();
                foreach (ConnInfo c in s.Connections)
                {
                    if (string.IsNullOrEmpty(c.Key)) continue;
                    ConnState prev;
                    long dd, du;
                    if (lastConn.TryGetValue(c.Key, out prev))
                    {
                        dd = Math.Max(0, c.Down - prev.Down);
                        du = Math.Max(0, c.Up - prev.Up);
                    }
                    else if (clashBaseline)
                    {
                        dd = du = 0;   // connections that existed before the dashboard opened
                    }
                    else
                    {
                        dd = c.Down;
                        du = c.Up;
                    }
                    seen[c.Key] = new ConnState { Down = c.Down, Up = c.Up, App = c.App, LastDelta = dd + du };
                    ConnSpeed[c.Key] = (dd + du) / secs;
                    if (dd == 0 && du == 0) continue;
                    Add(appDelta, c.App, dd, du);
                    attributedDown += dd;
                    attributedUp += du;
                }
                if (!clashBaseline)
                {
                    clashSeenDown += clashDown;
                    clashSeenUp += clashUp;
                }

                // A connection that closes between two polls takes its last bytes with it; they only
                // show up in sing-box's totals. The gap is kept cumulatively (per-connection counters can
                // lag the totals for a tick) and handed to the apps whose connections just closed.
                Dictionary<string, double> closed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, ConnState> kv in lastConn)
                {
                    if (seen.ContainsKey(kv.Key)) continue;
                    double w;
                    closed.TryGetValue(kv.Value.App, out w);
                    closed[kv.Value.App] = w + kv.Value.LastDelta + 1;
                }
                lastConn = seen;

                long gapD = Math.Max(0, clashSeenDown - attributedDown);
                long gapU = Math.Max(0, clashSeenUp - attributedUp);
                if (gapD > 0 || gapU > 0)
                {
                    Dictionary<string, double> target = null;
                    if (closed.Count > 0) target = closed;
                    else if (s.Connections.Count == 0 && ++idleTicks >= 3) target = new Dictionary<string, double>();   // -> Other
                    if (target != null)
                    {
                        Spread(appDelta, gapD, gapU, target);
                        attributedDown += gapD;
                        attributedUp += gapU;
                        idleTicks = 0;
                    }
                }
                else
                {
                    idleTicks = 0;
                }
            }
            else
            {
                lastConn.Clear();
                clashSeenDown = clashSeenUp = attributedDown = attributedUp = 0;
                idleTicks = 0;
                if (down > 0 || up > 0)
                {
                    // Rough: split by open tunnel connections per app.
                    Dictionary<string, double> w = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, int> kv in s.AppSockets) w[kv.Key] = kv.Value;
                    Spread(appDelta, down, up, w);
                }
            }

            if (!active) appDelta.Clear();

            // --- accumulate ----------------------------------------------------------------
            if (down > 0 || up > 0)
            {
                SessionDown += down;
                SessionUp += up;
                LifetimeDown += down;
                LifetimeUp += up;
                long[] day;
                if (!Daily.TryGetValue(Today, out day))
                {
                    day = new long[2];
                    Daily[Today] = day;
                }
                day[0] += down;
                day[1] += up;
                dirty = true;
            }
            foreach (KeyValuePair<string, long[]> kv in appDelta)
            {
                AppUsage a = GetApp(kv.Key);
                a.SessionDown += kv.Value[0];
                a.SessionUp += kv.Value[1];
                a.LifetimeDown += kv.Value[0];
                a.LifetimeUp += kv.Value[1];
                dirty = true;
            }

            SpeedDown = down / secs;
            SpeedUp = up / secs;
            Push(HistoryDown, SpeedDown);
            Push(HistoryUp, SpeedUp);

            if (++ticksSinceSave >= 15) Save();
        }

        public AppUsage GetApp(string name)
        {
            AppUsage a;
            if (!Apps.TryGetValue(name, out a))
            {
                a = new AppUsage();
                Apps[name] = a;
            }
            return a;
        }

        public void ResetSession()
        {
            SessionDown = SessionUp = 0;
            foreach (AppUsage a in Apps.Values) a.SessionDown = a.SessionUp = 0;
            SessionStart = DateTime.Now;
        }

        private static void Add(Dictionary<string, long[]> d, string app, long down, long up)
        {
            long[] v;
            if (!d.TryGetValue(app, out v))
            {
                v = new long[2];
                d[app] = v;
            }
            v[0] += down;
            v[1] += up;
        }

        /// <summary>Distributes bytes by weight; whatever cannot be placed goes to "Other".</summary>
        private static void Spread(Dictionary<string, long[]> target, long down, long up, Dictionary<string, double> weights)
        {
            double sum = 0;
            foreach (double v in weights.Values) sum += v;
            if (sum <= 0)
            {
                Add(target, OtherApp, down, up);
                return;
            }
            long placedD = 0, placedU = 0;
            string top = null;
            double topW = -1;
            foreach (KeyValuePair<string, double> kv in weights)
            {
                long d = (long)(down * kv.Value / sum);
                long u = (long)(up * kv.Value / sum);
                Add(target, kv.Key, d, u);
                placedD += d;
                placedU += u;
                if (kv.Value > topW) { topW = kv.Value; top = kv.Key; }
            }
            if (top != null) Add(target, top, down - placedD, up - placedU);   // rounding remainder
        }

        private static void Push(List<double> list, double v)
        {
            list.Add(v);
            while (list.Count > HistoryLength) list.RemoveAt(0);
        }

        // --- persistence ---------------------------------------------------------------------

        private void Load()
        {
            Dictionary<string, object> d = Json.ReadFile(StatsPath);
            if (d == null) return;
            object[] total = Json.Arr(d, "total");
            if (total != null && total.Length >= 2)
            {
                LifetimeDown = Json.ToLong(total[0]);
                LifetimeUp = Json.ToLong(total[1]);
            }
            DateTime since;
            if (DateTime.TryParse(Json.Str(d, "since"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out since)) TrackingSince = since;

            Dictionary<string, object> daily = Json.Obj(d, "daily");
            if (daily != null)
            {
                foreach (KeyValuePair<string, object> kv in daily)
                {
                    object[] v = kv.Value as object[];
                    if (v != null && v.Length >= 2) Daily[kv.Key] = new long[] { Json.ToLong(v[0]), Json.ToLong(v[1]) };
                }
            }
            Dictionary<string, object> apps = Json.Obj(d, "apps");
            if (apps != null)
            {
                foreach (KeyValuePair<string, object> kv in apps)
                {
                    object[] v = kv.Value as object[];
                    if (v == null || v.Length < 2) continue;
                    AppUsage a = GetApp(kv.Key);
                    a.LifetimeDown = Json.ToLong(v[0]);
                    a.LifetimeUp = Json.ToLong(v[1]);
                }
            }
        }

        public void Save()
        {
            ticksSinceSave = 0;
            if (!dirty && File.Exists(StatsPath)) return;
            try
            {
                // keep roughly four months of daily history
                List<string> old = new List<string>();
                string cutoff = DateTime.Now.AddDays(-120).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                foreach (string k in Daily.Keys) if (string.CompareOrdinal(k, cutoff) < 0) old.Add(k);
                foreach (string k in old) Daily.Remove(k);

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["version"] = 2;
                d["platform"] = Profile.Key;
                d["since"] = TrackingSince.ToString("o", CultureInfo.InvariantCulture);
                d["note"] = "Written by PhoneVPNDashboard. Totals are measured on the adapter that reaches the phone.";
                d["total"] = new long[] { LifetimeDown, LifetimeUp };
                Dictionary<string, object> daily = new Dictionary<string, object>();
                foreach (KeyValuePair<string, long[]> kv in Daily) daily[kv.Key] = new long[] { kv.Value[0], kv.Value[1] };
                d["daily"] = daily;
                Dictionary<string, object> apps = new Dictionary<string, object>();
                foreach (KeyValuePair<string, AppUsage> kv in Apps)
                {
                    if (kv.Value.LifetimeDown + kv.Value.LifetimeUp > 0) apps[kv.Key] = new long[] { kv.Value.LifetimeDown, kv.Value.LifetimeUp };
                }
                d["apps"] = apps;
                Json.WriteFileAtomic(StatsPath, Json.Stringify(d));
                dirty = false;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Stats save failed (" + StatsPath + "): " + ex.Message);
            }
        }
    }
}
