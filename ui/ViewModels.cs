using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PhoneVPNDashboard
{
    internal static class Fmt
    {
        public static string Bytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.CurrentCulture) + " GB";
        }

        public static string Speed(double bytesPerSec)
        {
            return Bytes((long)bytesPerSec) + "/s";
        }

        public static string Percent(double v)
        {
            return v.ToString("0.0", CultureInfo.CurrentCulture) + "%";
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            if (t.TotalHours >= 1) return string.Format("{0}h {1:00}m", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}m {1:00}s", (int)t.TotalMinutes, t.Seconds);
            return string.Format("{0}s", (int)t.TotalSeconds);
        }
    }

    internal static class Palette
    {
        private static readonly Color[] Colors =
        {
            Color.FromRgb(167, 139, 250), Color.FromRgb(45, 212, 191), Color.FromRgb(96, 165, 250),
            Color.FromRgb(244, 114, 182), Color.FromRgb(251, 191, 36), Color.FromRgb(74, 222, 128),
            Color.FromRgb(251, 146, 60), Color.FromRgb(56, 189, 248), Color.FromRgb(232, 121, 249),
            Color.FromRgb(163, 230, 53)
        };

        private static readonly Dictionary<string, Brush> Cache = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

        public static Brush For(string name)
        {
            Brush b;
            if (Cache.TryGetValue(name, out b)) return b;
            int h = 17;
            foreach (char c in name.ToLowerInvariant()) h = unchecked(h * 31 + c);
            Color col = string.Equals(name, UsageTracker.OtherApp, StringComparison.OrdinalIgnoreCase)
                ? Color.FromRgb(120, 126, 148)
                : Colors[(h & 0x7fffffff) % Colors.Length];
            b = new SolidColorBrush(col);
            b.Freeze();
            Cache[name] = b;
            return b;
        }
    }

    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>One app on the usage board (both the picker list and the comparison bars).</summary>
    public class AppVm : Observable
    {
        private bool isSelected;
        private long down, up;
        private string downText, upText, totalText, shareText, socketsText;
        private GridLength barFill = new GridLength(0, GridUnitType.Star);
        private GridLength barRest = new GridLength(1, GridUnitType.Star);

        public AppVm(string name)
        {
            Name = name;
            Initial = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            Brush = Palette.For(name);
        }

        public string Name { get; private set; }
        public string Initial { get; private set; }
        public Brush Brush { get; private set; }

        public event EventHandler SelectionChanged;

        public bool IsSelected
        {
            get { return isSelected; }
            set
            {
                if (isSelected == value) return;
                Set(ref isSelected, value, "IsSelected");
                EventHandler h = SelectionChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public void SetSelectedSilently(bool value)
        {
            Set(ref isSelected, value, "IsSelected");
        }

        public long Down { get { return down; } }
        public long Up { get { return up; } }
        public long Total { get { return down + up; } }

        public string DownText { get { return downText; } private set { Set(ref downText, value, "DownText"); } }
        public string UpText { get { return upText; } private set { Set(ref upText, value, "UpText"); } }
        public string TotalText { get { return totalText; } private set { Set(ref totalText, value, "TotalText"); } }
        public string ShareText { get { return shareText; } private set { Set(ref shareText, value, "ShareText"); } }
        public string SocketsText { get { return socketsText; } set { Set(ref socketsText, value, "SocketsText"); } }
        public GridLength BarFill { get { return barFill; } private set { Set(ref barFill, value, "BarFill"); } }
        public GridLength BarRest { get { return barRest; } private set { Set(ref barRest, value, "BarRest"); } }

        public void Update(long d, long u, long scopeTotal, long maxTotal)
        {
            down = d;
            up = u;
            DownText = Fmt.Bytes(d);
            UpText = Fmt.Bytes(u);
            TotalText = Fmt.Bytes(d + u);
            ShareText = scopeTotal > 0 ? Fmt.Percent((d + u) * 100.0 / scopeTotal) : "–";
            double frac = maxTotal > 0 ? (double)(d + u) / maxTotal : 0;
            if (d + u > 0 && frac < 0.01) frac = 0.01;
            BarFill = new GridLength(frac, GridUnitType.Star);
            BarRest = new GridLength(1 - frac, GridUnitType.Star);
        }
    }

    public class ConnVm : Observable
    {
        private string downText, upText, speedText, ageText, state;

        public ConnVm(string key, string app, string network, string destination, string route, bool tunnel)
        {
            Key = key;
            App = app;
            Network = network;
            Destination = destination;
            Route = route;
            Tunnel = tunnel;
            Brush = Palette.For(app);
        }

        public string Key { get; private set; }
        public string App { get; private set; }
        public string Network { get; private set; }
        public string Destination { get; private set; }
        public string Route { get; private set; }
        public bool Tunnel { get; private set; }
        public Brush Brush { get; private set; }
        public double SortValue;

        public string DownText { get { return downText; } set { Set(ref downText, value, "DownText"); } }
        public string UpText { get { return upText; } set { Set(ref upText, value, "UpText"); } }
        public string SpeedText { get { return speedText; } set { Set(ref speedText, value, "SpeedText"); } }
        public string AgeText { get { return ageText; } set { Set(ref ageText, value, "AgeText"); } }
        public string State { get { return state; } set { Set(ref state, value, "State"); } }
    }

    public class DayVm
    {
        public string Label { get; set; }
        public string Date { get; set; }
        public string TotalText { get; set; }
        public string Tip { get; set; }
        public GridLength BarFill { get; set; }
        public GridLength BarRest { get; set; }
        public Brush BarBrush { get; set; }
    }

    internal static class CollectionSync
    {
        /// <summary>Makes <paramref name="target"/> contain <paramref name="items"/> in that order, moving instead of rebuilding.</summary>
        public static void Sync<T>(ObservableCollection<T> target, IList<T> items) where T : class
        {
            HashSet<T> keep = new HashSet<T>(items);
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!keep.Contains(target[i])) target.RemoveAt(i);
            }
            for (int i = 0; i < items.Count; i++)
            {
                T item = items[i];
                int at = target.IndexOf(item);
                if (at < 0) target.Insert(i, item);
                else if (at != i) target.Move(at, i);
            }
        }
    }
}
