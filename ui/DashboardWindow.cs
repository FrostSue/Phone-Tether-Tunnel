using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace PhoneVPNDashboard
{
    public class DashboardWindow : Window
    {
        private static readonly CultureInfo En = new CultureInfo("en-US");
        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
        private static readonly string[] LogLevels = { "debug", "info", "warn", "error" };

        private readonly FrameworkElement root;
        private readonly Prefs prefs;
        private readonly Collector collector = new Collector();
        private PlatformProfile profile;
        private string folder;
        private UsageTracker tracker;
        private Snapshot last;
        private string page = "overview";
        private bool suppressPlatformEvent;
        private bool suppressLogLevelEvent;

        // board
        private readonly Dictionary<string, AppVm> boardVms = new Dictionary<string, AppVm>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AppVm> topVms = new Dictionary<string, AppVm>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<AppVm> pickList = new ObservableCollection<AppVm>();
        private readonly ObservableCollection<AppVm> selectedBars = new ObservableCollection<AppVm>();
        private readonly ObservableCollection<AppVm> topApps = new ObservableCollection<AppVm>();
        private readonly ObservableCollection<DayVm> days = new ObservableCollection<DayVm>();
        private HashSet<string> savedSelection;
        private bool defaultSelectionDone;
        private bool scopeAll;
        private int resortCountdown;
        private string daysSignature;

        // connections
        private readonly Dictionary<string, ConnVm> connVms = new Dictionary<string, ConnVm>();
        private readonly ObservableCollection<ConnVm> conns = new ObservableCollection<ConnVm>();

        // logs
        private readonly StringBuilder dashLog = new StringBuilder();
        private readonly DispatcherTimer logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        private LogSource currentLog;
        private string currentLogLabel = "Watcher";
        private long logPos = -1;
        private bool logMissingShown;

        public DashboardWindow()
        {
            Title = "Phone-Tether-Tunnel";
            Width = 1280;
            Height = 860;
            MinWidth = 1100;
            MinHeight = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            root = LoadXaml();
            Content = root;
            Background = (Brush)root.FindResource("BgBrush");
            ((Panel)root).Background = Background;
            TextElement.SetForeground(root, (Brush)root.FindResource("TextBrush"));
            LoadIcon();
            SourceInitialized += (s, e) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);

            prefs = Prefs.Load();
            WireEvents();

            F<ItemsControl>("AppPickList").ItemsSource = pickList;
            F<ItemsControl>("SelectedBars").ItemsSource = selectedBars;
            F<ItemsControl>("OvTopApps").ItemsSource = topApps;
            F<ItemsControl>("OvDays").ItemsSource = days;
            F<ItemsControl>("ConnList").ItemsSource = conns;
            F<Canvas>("SpeedCanvas").SizeChanged += (s, e) => DrawChart();

            PlatformProfile start = PlatformLocator.DetectRunning() ?? PlatformProfile.ByKey(prefs.Platform) ?? PlatformProfile.Android;
            SwitchPlatform(start);

            collector.SnapshotReady += s => Dispatcher.BeginInvoke(new Action(() => ApplySnapshot(s)));
            collector.Error += msg => Dispatcher.BeginInvoke(new Action(() => AppendDash("collector: " + msg)));
            collector.Start();

            logTimer.Tick += (s, e) => { if (page == "logs") ReadLogTick(); };
            logTimer.Start();

            Closing += OnClosing;
        }

        private T F<T>(string name) where T : class
        {
            T el = root.FindName(name) as T;
            if (el == null) throw new InvalidOperationException("UI element missing: " + name);
            return el;
        }

        private static FrameworkElement LoadXaml()
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml");
            if (s == null)
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "MainWindow.xaml");
                s = File.OpenRead(p);
            }
            using (s) return (FrameworkElement)XamlReader.Load(s);
        }

        private void LoadIcon()
        {
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (s == null) return;
                IconBitmapDecoder dec = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Icon = dec.Frames[0];
                BitmapFrame logo = dec.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 128)).First();
                F<Image>("LogoImage").Source = logo;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Icon load failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ wiring

        private void WireEvents()
        {
            F<RadioButton>("PlatformAndroid").Checked += (s, e) => { if (!suppressPlatformEvent) SwitchPlatform(PlatformProfile.Android); };
            F<RadioButton>("PlatformIphone").Checked += (s, e) => { if (!suppressPlatformEvent) SwitchPlatform(PlatformProfile.Iphone); };

            F<RadioButton>("NavOverview").Checked += (s, e) => ShowPage("overview");
            F<RadioButton>("NavUsage").Checked += (s, e) => ShowPage("usage");
            F<RadioButton>("NavConnections").Checked += (s, e) => ShowPage("connections");
            F<RadioButton>("NavLogs").Checked += (s, e) => ShowPage("logs");
            F<RadioButton>("NavSettings").Checked += (s, e) => ShowPage("settings");
            F<Button>("OvOpenBoard").Click += (s, e) => F<RadioButton>("NavUsage").IsChecked = true;

            F<Button>("StartBtn").Click += (s, e) => RunElevated("start.ps1", "Start");
            F<Button>("StopBtn").Click += (s, e) =>
            {
                MessageBoxResult r = MessageBox.Show(this, "Stop the " + profile.DisplayName + " tunnel?\n\nTraffic will no longer go through the phone tunnel until you start it again.",
                    "Stop tunnel", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (r == MessageBoxResult.Yes) RunElevated("stop.ps1", "Stop");
            };
            F<Button>("TestBtn").Click += (s, e) => RunTest();

            F<TextBox>("AppSearch").TextChanged += (s, e) => { resortCountdown = 0; UpdateBoard(true); };
            F<Button>("SelectAllBtn").Click += (s, e) => SetSelection(pickList.Select(v => v.Name));
            F<Button>("SelectNoneBtn").Click += (s, e) => SetSelection(new string[0]);
            F<Button>("SelectTopBtn").Click += (s, e) => SetSelection(TopApps(5, scopeAll));
            F<RadioButton>("ScopeSession").Checked += (s, e) => { scopeAll = false; resortCountdown = 0; UpdateBoard(true); };
            F<RadioButton>("ScopeAll").Checked += (s, e) => { scopeAll = true; resortCountdown = 0; UpdateBoard(true); };
            F<Button>("ResetSessionBtn").Click += (s, e) =>
            {
                tracker.ResetSession();
                AppendDash("new session started");
                RefreshViews();
            };

            F<TextBox>("ConnSearch").TextChanged += (s, e) => UpdateConnections();
            F<RadioButton>("ConnAll").Checked += (s, e) => UpdateConnections();
            F<RadioButton>("ConnTunnel").Checked += (s, e) => UpdateConnections();
            F<RadioButton>("ConnDirect").Checked += (s, e) => UpdateConnections();

            F<Button>("ClearLogBtn").Click += (s, e) => F<TextBox>("LogBox").Clear();
            F<Button>("OpenFolderBtn").Click += (s, e) => OpenFolder();
            F<CheckBox>("AutoScroll").Checked += (s, e) => F<TextBox>("LogBox").ScrollToEnd();

            F<Button>("BrowseBtn").Click += (s, e) => BrowseFolder();
            F<Button>("DetectBtn").Click += (s, e) =>
            {
                F<TextBox>("FolderBox").Text = PlatformLocator.Detect(profile);
                UpdateFolderHint();
            };
            F<TextBox>("FolderBox").TextChanged += (s, e) => UpdateFolderHint();
            F<Button>("ApplyFolderBtn").Click += (s, e) => ApplyFolder();
            F<Button>("EnableClashBtn").Click += (s, e) => EnableClash();
            F<Button>("RestartBtn").Click += (s, e) => RunElevated("start.ps1", "Restart");
            F<Button>("SaveIphoneBtn").Click += (s, e) => SaveIphoneSettings();
        }

        // ------------------------------------------------------------------ platform

        private void SwitchPlatform(PlatformProfile p)
        {
            if (tracker != null)
            {
                tracker.Save();
                SaveSelection();
            }

            profile = p;
            prefs.Platform = p.Key;
            string over;
            prefs.Folders.TryGetValue(p.Key, out over);
            folder = PlatformLocator.Resolve(p, over);
            tracker = new UsageTracker(p, folder);
            last = null;

            boardVms.Clear();
            topVms.Clear();
            pickList.Clear();
            selectedBars.Clear();
            topApps.Clear();
            conns.Clear();
            connVms.Clear();
            daysSignature = null;
            List<string> sel;
            savedSelection = prefs.Selected.TryGetValue(p.Key, out sel) && sel.Count > 0 ? new HashSet<string>(sel, StringComparer.OrdinalIgnoreCase) : null;
            defaultSelectionDone = savedSelection != null;
            userTouchedSelection = false;
            prefs.Save();

            suppressPlatformEvent = true;
            F<RadioButton>(p == PlatformProfile.Android ? "PlatformAndroid" : "PlatformIphone").IsChecked = true;
            suppressPlatformEvent = false;

            collector.Configure(p, folder);
            F<TextBlock>("SideFolder").Text = folder;
            F<TextBlock>("SideFolder").ToolTip = folder;
            AppendDash(string.Format("platform: {0}, folder: {1}{2}", p.Title, folder,
                PlatformLocator.IsInstallFolder(folder) ? "" : " (no install found here)"));
            AppendDash("usage statistics: " + tracker.StatsPath);

            BuildLogSources();
            LoadSettingsPage();
            UpdatePageHeader();
            RefreshViews();
        }

        private void ShowPage(string key)
        {
            page = key;
            F<FrameworkElement>("PageOverview").Visibility = key == "overview" ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("PageUsage").Visibility = key == "usage" ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("PageConnections").Visibility = key == "connections" ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("PageLogs").Visibility = key == "logs" ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("PageSettings").Visibility = key == "settings" ? Visibility.Visible : Visibility.Collapsed;
            UpdatePageHeader();
            if (key == "logs") ReadLogTick();
            if (key == "settings") LoadSettingsPage();
            RefreshViews();
        }

        private void UpdatePageHeader()
        {
            string title, sub;
            switch (page)
            {
                case "usage":
                    title = "Data usage";
                    sub = "Session, daily and all-time usage — pick apps to compare them on the board";
                    break;
                case "connections":
                    title = "Connections";
                    sub = "What apps on this PC are talking to right now, and whether it goes through the tunnel";
                    break;
                case "logs":
                    title = "Logs";
                    sub = "Watcher, sing-box and Xray logs from the install folder";
                    break;
                case "settings":
                    title = "Settings";
                    sub = profile.Title;
                    break;
                default:
                    title = "Overview";
                    sub = profile.Title + " tunnel";
                    break;
            }
            F<TextBlock>("PageTitle").Text = title;
            F<TextBlock>("PageSubtitle").Text = sub;
        }

        // ------------------------------------------------------------------ snapshots

        private void ApplySnapshot(Snapshot s)
        {
            if (s.Profile != profile || s.Folder != folder) return;   // taken before a platform switch
            last = s;
            tracker.Apply(s);
            RefreshViews();
        }

        private void RefreshViews()
        {
            UpdateStatus();
            if (page == "overview") UpdateOverview();
            if (page == "usage") UpdateUsage();
            if (page == "connections") UpdateConnections();
            if (page == "settings") UpdateClashStatus();
            UpdateBoard(false);
        }

        private void UpdateStatus()
        {
            Brush good = (Brush)root.FindResource("GoodBrush");
            Brush warn = (Brush)root.FindResource("WarnBrush");
            Brush bad = (Brush)root.FindResource("BadBrush");
            Brush faint = (Brush)root.FindResource("FaintBrush");

            string status, cores;
            Brush dot;
            if (last == null)
            {
                status = "Checking…";
                cores = "";
                dot = faint;
            }
            else if (last.SingboxRunning && last.XrayRunning)
            {
                status = "Connected";
                cores = "sing-box and Xray running";
                dot = good;
            }
            else if (last.SingboxRunning || last.XrayRunning)
            {
                status = "Starting";
                cores = last.SingboxRunning ? "sing-box running, Xray not yet" : "Xray running, sing-box not yet";
                dot = warn;
            }
            else
            {
                status = "Stopped";
                cores = last.OtherRunning != null ? last.OtherRunning.DisplayName + " tunnel is running instead" : "Tunnel is not running";
                dot = bad;
            }

            F<Ellipse>("OvDot").Fill = dot;
            F<TextBlock>("OvStatus").Text = status;
            F<TextBlock>("OvCores").Text = cores;
            F<Ellipse>("SideDot").Fill = dot;
            F<TextBlock>("SideStatus").Text = profile.DisplayName + " · " + status;
            F<TextBlock>("SideSpeed").Text = last != null && last.SingboxRunning
                ? "↓ " + Fmt.Speed(tracker.SpeedDown) + "   ↑ " + Fmt.Speed(tracker.SpeedUp)
                : "";
            F<TextBlock>("StartBtnText").Text = last != null && (last.SingboxRunning || last.XrayRunning) ? "Restart" : "Start";

            PhoneLink ph = last != null ? last.Phone : null;
            if (ph != null && !string.IsNullOrEmpty(ph.Ip))
            {
                F<TextBlock>("OvPhone").Text = ph.Ip + (ph.Port > 0 ? ":" + ph.Port : "");
                F<TextBlock>("OvLink").Text = ph.AdapterName != null ? ph.Kind + " · " + ph.AdapterName : "Adapter to the phone not found";
            }
            else
            {
                F<TextBlock>("OvPhone").Text = "Not found";
                F<TextBlock>("OvLink").Text = "Waiting for the watcher to find the phone";
            }
        }

        // ------------------------------------------------------------------ overview

        private void UpdateOverview()
        {
            F<TextBlock>("OvDown").Text = Fmt.Speed(tracker.SpeedDown);
            F<TextBlock>("OvUp").Text = Fmt.Speed(tracker.SpeedUp) + " upload";
            long[] today = tracker.TodayTotals;
            F<TextBlock>("OvToday").Text = Fmt.Bytes(today[0] + today[1]);
            F<TextBlock>("OvTodaySplit").Text = "↓ " + Fmt.Bytes(today[0]) + "   ↑ " + Fmt.Bytes(today[1]);
            F<TextBlock>("OvSource").Text = "Measured on: " + tracker.Source + "   ·   per-app split: " + (tracker.ExactApps ? "exact (Clash API)" : "rough estimate by open connections");

            // Top apps this session
            long sessTotal = 0, max = 0;
            List<KeyValuePair<string, AppUsage>> top = tracker.Apps
                .Where(kv => kv.Value.SessionDown + kv.Value.SessionUp > 0)
                .OrderByDescending(kv => kv.Value.SessionDown + kv.Value.SessionUp)
                .Take(5).ToList();
            foreach (AppUsage a in tracker.Apps.Values) sessTotal += a.SessionDown + a.SessionUp;
            if (top.Count > 0) max = top[0].Value.SessionDown + top[0].Value.SessionUp;
            List<AppVm> items = new List<AppVm>();
            foreach (KeyValuePair<string, AppUsage> kv in top)
            {
                AppVm vm;
                if (!topVms.TryGetValue(kv.Key, out vm))
                {
                    vm = new AppVm(kv.Key);
                    topVms[kv.Key] = vm;
                }
                vm.Update(kv.Value.SessionDown, kv.Value.SessionUp, sessTotal, max);
                items.Add(vm);
            }
            CollectionSync.Sync(topApps, items);
            F<TextBlock>("OvTopEmpty").Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateDays();
            DrawChart();
        }

        private void UpdateDays()
        {
            DateTime today = DateTime.Today;
            long[][] vals = new long[7][];
            long max = 0;
            StringBuilder sig = new StringBuilder();
            for (int i = 0; i < 7; i++)
            {
                DateTime d = today.AddDays(i - 6);
                long[] v;
                if (!tracker.Daily.TryGetValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out v)) v = new long[2];
                vals[i] = v;
                max = Math.Max(max, v[0] + v[1]);
                sig.Append(Fmt.Bytes(v[0] + v[1])).Append('|');
            }
            string signature = sig.ToString() + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (signature == daysSignature) return;
            daysSignature = signature;

            Brush todayBrush = (Brush)root.FindResource("AccentBrush");
            Brush otherBrush = new SolidColorBrush(Color.FromRgb(76, 61, 128));
            days.Clear();
            for (int i = 0; i < 7; i++)
            {
                DateTime d = today.AddDays(i - 6);
                long tot = vals[i][0] + vals[i][1];
                double frac = max > 0 ? (double)tot / max : 0;
                if (tot > 0 && frac < 0.03) frac = 0.03;
                days.Add(new DayVm
                {
                    Label = i == 6 ? "Today" : d.ToString("ddd", En),
                    Date = d.ToString("d MMM", En),
                    TotalText = tot > 0 ? Fmt.Bytes(tot) : "",
                    Tip = d.ToString("dddd d MMMM", En) + "\n↓ " + Fmt.Bytes(vals[i][0]) + "   ↑ " + Fmt.Bytes(vals[i][1]),
                    BarFill = new GridLength(frac, GridUnitType.Star),
                    BarRest = new GridLength(1 - frac, GridUnitType.Star),
                    BarBrush = i == 6 ? todayBrush : otherBrush
                });
            }
        }

        private void DrawChart()
        {
            if (page != "overview" || tracker == null) return;
            Canvas c = F<Canvas>("SpeedCanvas");
            double w = c.ActualWidth, h = c.ActualHeight;
            if (w < 40 || h < 40) return;
            c.Children.Clear();

            const double left = 64, top = 6, bottom = 4;
            double plotW = w - left - 4, plotH = h - top - bottom;
            double max = 64 * 1024;
            foreach (double v in tracker.HistoryDown) max = Math.Max(max, v);
            foreach (double v in tracker.HistoryUp) max = Math.Max(max, v);
            max = NiceCeiling(max * 1.1);

            Brush grid = new SolidColorBrush(Color.FromArgb(40, 140, 146, 168));
            Brush label = (Brush)root.FindResource("FaintBrush");
            for (int i = 0; i <= 3; i++)
            {
                double y = top + plotH * i / 3.0;
                c.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = left, X2 = w - 4, Y1 = y, Y2 = y,
                    Stroke = grid, StrokeThickness = 1,
                    StrokeDashArray = i == 3 ? null : new DoubleCollection(new double[] { 3, 4 })
                });
                TextBlock t = new TextBlock { Text = Fmt.Speed(max * (3 - i) / 3.0), FontSize = 10.5, Foreground = label };
                Canvas.SetLeft(t, 0);
                Canvas.SetTop(t, y - 8);
                c.Children.Add(t);
            }

            DrawSeries(c, tracker.HistoryDown, Color.FromRgb(167, 139, 250), left, top, plotW, plotH, max);
            DrawSeries(c, tracker.HistoryUp, Color.FromRgb(45, 212, 191), left, top, plotW, plotH, max);
        }

        private static void DrawSeries(Canvas c, List<double> data, Color color, double left, double top, double w, double h, double max)
        {
            int n = data.Count;
            if (n < 2) return;
            PointCollection line = new PointCollection(n);
            PointCollection area = new PointCollection(n + 2);
            for (int i = 0; i < n; i++)
            {
                double x = left + w * i / (n - 1);
                double y = top + h - h * Math.Min(1.0, data[i] / max);
                line.Add(new Point(x, y));
                area.Add(new Point(x, y));
            }
            area.Add(new Point(left + w, top + h));
            area.Add(new Point(left, top + h));

            LinearGradientBrush fill = new LinearGradientBrush(Color.FromArgb(90, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
            c.Children.Add(new System.Windows.Shapes.Polygon { Points = area, Fill = fill });
            c.Children.Add(new System.Windows.Shapes.Polyline
            {
                Points = line,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            });
        }

        private static double NiceCeiling(double v)
        {
            double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (double m in new double[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            {
                if (m * exp >= v) return m * exp;
            }
            return 10 * exp;
        }

        // ------------------------------------------------------------------ usage + board

        private void UpdateUsage()
        {
            F<TextBlock>("UsSession").Text = Fmt.Bytes(tracker.SessionDown + tracker.SessionUp);
            F<TextBlock>("UsSessionSplit").Text = "↓ " + Fmt.Bytes(tracker.SessionDown) + "   ↑ " + Fmt.Bytes(tracker.SessionUp) + "   · since " + tracker.SessionStart.ToString("HH:mm", En);
            long[] today = tracker.TodayTotals;
            F<TextBlock>("UsToday").Text = Fmt.Bytes(today[0] + today[1]);
            F<TextBlock>("UsTodaySplit").Text = "↓ " + Fmt.Bytes(today[0]) + "   ↑ " + Fmt.Bytes(today[1]);
            F<TextBlock>("UsTotal").Text = Fmt.Bytes(tracker.LifetimeDown + tracker.LifetimeUp);
            F<TextBlock>("UsTotalSplit").Text = "↓ " + Fmt.Bytes(tracker.LifetimeDown) + "   ↑ " + Fmt.Bytes(tracker.LifetimeUp) + "   · since " + tracker.TrackingSince.ToString("d MMM", En);
            F<TextBlock>("UsSource").Text = tracker.Source;
            F<TextBlock>("UsSource").ToolTip = tracker.Source;
            F<TextBlock>("UsMode").Text = tracker.ExactApps
                ? "Per-app split is exact (sing-box Clash API)."
                : "Per-app split is a rough estimate (by open connections). Enable exact tracking in Settings.";
        }

        private IEnumerable<string> TopApps(int count, bool all)
        {
            return tracker.Apps
                .OrderByDescending(kv => all ? kv.Value.LifetimeDown + kv.Value.LifetimeUp : kv.Value.SessionDown + kv.Value.SessionUp)
                .Where(kv => (all ? kv.Value.LifetimeDown + kv.Value.LifetimeUp : kv.Value.SessionDown + kv.Value.SessionUp) > 0)
                .Take(count)
                .Select(kv => kv.Key)
                .ToList();
        }

        private void UpdateBoard(bool force)
        {
            if (tracker == null) return;

            // Default selection: the five biggest apps, once there is data.
            if (!defaultSelectionDone && tracker.Apps.Count > 0)
            {
                List<string> top = TopApps(5, true).ToList();
                if (top.Count > 0)
                {
                    savedSelection = new HashSet<string>(top, StringComparer.OrdinalIgnoreCase);
                    defaultSelectionDone = true;
                }
            }

            long scopeTotal = 0;
            foreach (AppUsage a in tracker.Apps.Values)
            {
                scopeTotal += scopeAll ? a.LifetimeDown + a.LifetimeUp : a.SessionDown + a.SessionUp;
            }

            bool membershipChanged = false;
            foreach (KeyValuePair<string, AppUsage> kv in tracker.Apps)
            {
                AppVm vm;
                if (!boardVms.TryGetValue(kv.Key, out vm))
                {
                    vm = new AppVm(kv.Key);
                    if (savedSelection != null && savedSelection.Contains(kv.Key)) vm.SetSelectedSilently(true);
                    vm.SelectionChanged += OnAppSelectionChanged;
                    boardVms[kv.Key] = vm;
                    membershipChanged = true;
                }
                else if (savedSelection != null && !vm.IsSelected && savedSelection.Contains(kv.Key) && !userTouchedSelection)
                {
                    vm.SetSelectedSilently(true);
                }
            }

            List<AppVm> selected = boardVms.Values.Where(v => v.IsSelected).ToList();
            long maxSel = 0;
            foreach (AppVm vm in boardVms.Values)
            {
                AppUsage a = tracker.Apps[vm.Name];
                long d = scopeAll ? a.LifetimeDown : a.SessionDown;
                long u = scopeAll ? a.LifetimeUp : a.SessionUp;
                if (vm.IsSelected) maxSel = Math.Max(maxSel, d + u);
            }
            foreach (AppVm vm in boardVms.Values)
            {
                AppUsage a = tracker.Apps[vm.Name];
                vm.Update(scopeAll ? a.LifetimeDown : a.SessionDown, scopeAll ? a.LifetimeUp : a.SessionUp, scopeTotal, maxSel);
            }

            // Picker: re-sort at most every 5 s and never under the mouse, so rows do not jump while clicking.
            string filter = F<TextBox>("AppSearch").Text.Trim();
            ItemsControl picker = F<ItemsControl>("AppPickList");
            if (force || membershipChanged || (--resortCountdown <= 0 && !picker.IsMouseOver))
            {
                resortCountdown = 5;
                List<AppVm> list = boardVms.Values
                    .Where(v => filter.Length == 0 || v.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(v => v.Total).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                CollectionSync.Sync(pickList, list);
            }

            CollectionSync.Sync(selectedBars, selected.OrderByDescending(v => v.Total).ThenBy(v => v.Name).ToList());

            long selD = selected.Sum(v => v.Down), selU = selected.Sum(v => v.Up);
            F<TextBlock>("SelTitle").Text = selected.Count == 1 ? "SELECTED · 1 APP" : "SELECTED · " + selected.Count + " APPS";
            F<TextBlock>("SelTotal").Text = Fmt.Bytes(selD + selU);
            F<TextBlock>("SelDown").Text = Fmt.Bytes(selD);
            F<TextBlock>("SelUp").Text = Fmt.Bytes(selU);
            F<TextBlock>("SelShare").Text = scopeTotal > 0 ? Fmt.Percent((selD + selU) * 100.0 / scopeTotal) : "–";
            F<TextBlock>("SelEmpty").Visibility = selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            F<TextBlock>("BoardSubtitle").Text = (scopeAll
                    ? "All time, since " + tracker.TrackingSince.ToString("d MMM yyyy", En)
                    : "This session, since " + tracker.SessionStart.ToString("HH:mm", En))
                + " · " + boardVms.Count + " apps seen" + (tracker.ExactApps ? " · exact" : " · rough estimate");
        }

        private bool userTouchedSelection;

        private void OnAppSelectionChanged(object sender, EventArgs e)
        {
            userTouchedSelection = true;
            SaveSelection();
            prefs.Save();
            UpdateBoard(false);
        }

        private void SetSelection(IEnumerable<string> names)
        {
            HashSet<string> want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            userTouchedSelection = true;
            foreach (AppVm vm in boardVms.Values) vm.SetSelectedSilently(want.Contains(vm.Name));
            SaveSelection();
            prefs.Save();
            UpdateBoard(true);
        }

        private void SaveSelection()
        {
            if (profile == null) return;
            if (boardVms.Count == 0 && savedSelection != null) return;   // nothing loaded yet; keep what was saved
            prefs.Selected[profile.Key] = boardVms.Values.Where(v => v.IsSelected).Select(v => v.Name).ToList();
        }

        // ------------------------------------------------------------------ connections

        private void UpdateConnections()
        {
            if (last == null || page != "connections") return;
            bool exact = last.ClashOk;
            string filter = F<TextBox>("ConnSearch").Text.Trim();
            bool onlyTunnel = F<RadioButton>("ConnTunnel").IsChecked == true;
            bool onlyDirect = F<RadioButton>("ConnDirect").IsChecked == true;

            F<TextBlock>("ConnColAge").Text = exact ? "AGE" : "STATE";
            F<TextBlock>("ConnColSpeed").Text = exact ? "SPEED" : "";
            F<TextBlock>("ConnColDown").Text = exact ? "DOWN" : "";
            F<TextBlock>("ConnColUp").Text = exact ? "UP" : "";

            List<ConnVm> items = new List<ConnVm>();
            HashSet<string> alive = new HashSet<string>();
            int tunnelCount = 0;
            foreach (ConnInfo c in last.Connections)
            {
                if (c.Tunnel) tunnelCount++;
                if (onlyTunnel && !c.Tunnel) continue;
                if (onlyDirect && c.Tunnel) continue;
                if (filter.Length > 0
                    && c.App.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && c.Destination.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                ConnVm vm;
                if (!connVms.TryGetValue(c.Key, out vm))
                {
                    vm = new ConnVm(c.Key, c.App, c.Network, c.Destination, c.Route, c.Tunnel);
                    connVms[c.Key] = vm;
                }
                alive.Add(c.Key);
                if (exact)
                {
                    double sp;
                    tracker.ConnSpeed.TryGetValue(c.Key, out sp);
                    vm.DownText = Fmt.Bytes(c.Down);
                    vm.UpText = Fmt.Bytes(c.Up);
                    vm.SpeedText = sp >= 1 ? Fmt.Speed(sp) : "";
                    vm.AgeText = c.Start == DateTime.MinValue ? "" : Fmt.Duration(DateTime.Now - c.Start);
                    vm.SortValue = sp * 1e6 + c.Down + c.Up;
                }
                else
                {
                    vm.DownText = vm.UpText = vm.SpeedText = "";
                    vm.AgeText = c.State;
                    vm.SortValue = c.Tunnel ? 1 : 0;
                }
                items.Add(vm);
            }
            foreach (string k in connVms.Keys.Where(k => !alive.Contains(k)).ToList()) connVms.Remove(k);

            List<ConnVm> sorted = exact
                ? items.OrderByDescending(v => v.SortValue).Take(400).ToList()
                : items.OrderByDescending(v => v.SortValue).ThenBy(v => v.App, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Destination).Take(400).ToList();
            CollectionSync.Sync(conns, sorted);

            F<TextBlock>("ConnEmpty").Visibility = sorted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            F<TextBlock>("ConnInfo").Text = string.Format("{0} connections · {1} through the tunnel · {2}",
                last.Connections.Count, tunnelCount, exact ? "live bytes from sing-box" : "TCP table (enable exact tracking for bytes and UDP)");
        }

        // ------------------------------------------------------------------ logs

        private void BuildLogSources()
        {
            StackPanel panel = F<StackPanel>("LogSources");
            panel.Children.Clear();
            Style seg = (Style)root.FindResource("Seg");
            RadioButton first = null, match = null;
            foreach (LogSource src in profile.Logs)
            {
                LogSource captured = src;
                RadioButton rb = new RadioButton { Content = src.Label, GroupName = "logsrc", Style = seg };
                rb.Checked += (s, e) => SelectLog(captured);
                panel.Children.Add(rb);
                if (first == null) first = rb;
                if (src.Label == currentLogLabel) match = rb;
            }
            RadioButton pick = match ?? first;
            if (pick != null)
            {
                if (pick.IsChecked == true) pick.IsChecked = false;
                pick.IsChecked = true;
            }
        }

        private void SelectLog(LogSource src)
        {
            currentLog = src;
            currentLogLabel = src.Label;
            logPos = -1;
            logMissingShown = false;
            TextBox box = F<TextBox>("LogBox");
            box.Clear();
            if (src.RelativePath == null)
            {
                F<TextBlock>("LogPath").Text = "Messages from this dashboard (start/stop requests, diagnostics output)";
                box.Text = dashLog.ToString();
                box.ScrollToEnd();
                return;
            }
            F<TextBlock>("LogPath").Text = Path.Combine(folder, src.RelativePath);
            ReadLogTick();
        }

        private void ReadLogTick()
        {
            if (currentLog == null || currentLog.RelativePath == null) return;
            TextBox box = F<TextBox>("LogBox");
            string path = Path.Combine(folder, currentLog.RelativePath);
            try
            {
                if (!File.Exists(path))
                {
                    if (!logMissingShown)
                    {
                        box.Text = "File not found: " + path + "\n\nIt is created when the tunnel runs.";
                        logMissingShown = true;
                    }
                    return;
                }
                if (logMissingShown)
                {
                    box.Clear();
                    logMissingShown = false;
                }

                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    if (logPos < 0 || len < logPos)
                    {
                        logPos = Math.Max(0, len - 96 * 1024);
                        box.Clear();
                    }
                    if (len == logPos) return;
                    long start = Math.Max(logPos, len - 1024 * 1024);
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[len - start];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    logPos = start + read;
                    string text = Ansi.Replace(Encoding.UTF8.GetString(buf, 0, read), "");
                    box.AppendText(text);
                    if (box.Text.Length > 400000) box.Text = box.Text.Substring(box.Text.Length - 250000);
                    if (F<CheckBox>("AutoScroll").IsChecked == true) box.ScrollToEnd();
                }
            }
            catch (Exception ex)
            {
                box.AppendText("\n[dashboard] could not read log: " + ex.Message + "\n");
            }
        }

        private void AppendDash(string message)
        {
            string line = string.Format("[{0:HH:mm:ss}] {1}\n", DateTime.Now, message);
            dashLog.Append(line);
            if (dashLog.Length > 200000) dashLog.Remove(0, dashLog.Length - 150000);
            if (currentLog != null && currentLog.RelativePath == null && root != null)
            {
                TextBox box = F<TextBox>("LogBox");
                box.AppendText(line);
                if (F<CheckBox>("AutoScroll").IsChecked == true) box.ScrollToEnd();
            }
        }

        private void ShowDashboardLog()
        {
            F<RadioButton>("NavLogs").IsChecked = true;
            foreach (object o in F<StackPanel>("LogSources").Children)
            {
                RadioButton rb = o as RadioButton;
                if (rb != null && (string)rb.Content == "Dashboard") rb.IsChecked = true;
            }
        }

        // ------------------------------------------------------------------ actions

        private void RunElevated(string script, string label)
        {
            string path = Path.Combine(folder, script);
            if (!File.Exists(path))
            {
                AppendDash(label + ": " + path + " not found");
                MessageBox.Show(this, script + " was not found in\n" + folder + "\n\nPick the install folder in Settings.", label,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + path + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = folder,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                Process.Start(psi);
                AppendDash(label + ": " + script + " started as administrator");
                collector.Refresh();
            }
            catch (Win32Exception)
            {
                AppendDash(label + ": cancelled (administrator permission was not given)");
            }
            catch (Exception ex)
            {
                AppendDash(label + " failed: " + ex.Message);
            }
        }

        private void RunTest()
        {
            string path = Path.Combine(folder, "test.ps1");
            if (!File.Exists(path))
            {
                AppendDash("Diagnose: " + path + " not found");
                ShowDashboardLog();
                return;
            }
            Encoding oem;
            try { oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { oem = Encoding.Default; }
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = folder,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem
            };
            Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) Dispatcher.BeginInvoke(new Action(() => AppendDash("  " + e.Data))); };
            proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Dispatcher.BeginInvoke(new Action(() => AppendDash("  [err] " + e.Data))); };
            proc.Exited += (s, e) => Dispatcher.BeginInvoke(new Action(() => AppendDash("Diagnose finished (exit code " + SafeExit(proc) + ")")));
            try
            {
                AppendDash("Diagnose: running " + path);
                ShowDashboardLog();
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AppendDash("Diagnose failed to start: " + ex.Message);
            }
        }

        private static string SafeExit(Process p)
        {
            try { return p.ExitCode.ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }

        private void OpenFolder()
        {
            try
            {
                if (Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex)
            {
                AppendDash("Open folder failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ settings

        private void LoadSettingsPage()
        {
            F<TextBlock>("FolderCardTitle").Text = profile.Title + " install folder";
            F<TextBox>("FolderBox").Text = folder;
            UpdateFolderHint();
            UpdateClashStatus();

            // log level
            Dictionary<string, object> cfg = Json.ReadFile(Path.Combine(folder, profile.SingboxConfig));
            string level = Json.Str(Json.Obj(cfg, "log"), "level") ?? "";
            StackPanel lp = F<StackPanel>("LogLevelPanel");
            lp.Children.Clear();
            Style seg = (Style)root.FindResource("Seg");
            suppressLogLevelEvent = true;
            foreach (string l in LogLevels)
            {
                string captured = l;
                RadioButton rb = new RadioButton { Content = l, GroupName = "loglevel", Style = seg, IsEnabled = cfg != null };
                rb.IsChecked = string.Equals(l, level, StringComparison.OrdinalIgnoreCase);
                rb.Checked += (s, e) => { if (!suppressLogLevelEvent) WriteLogLevel(captured); };
                lp.Children.Add(rb);
            }
            suppressLogLevelEvent = false;

            bool iphone = profile == PlatformProfile.Iphone;
            F<FrameworkElement>("IphoneCard").Visibility = iphone ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("AndroidCard").Visibility = iphone ? Visibility.Collapsed : Visibility.Visible;
            if (iphone) LoadIphoneSettings();
        }

        private void UpdateFolderHint()
        {
            string path = F<TextBox>("FolderBox").Text.Trim();
            TextBlock hint = F<TextBlock>("FolderHint");
            if (PlatformLocator.IsInstallFolder(path))
            {
                hint.Text = "✓ Install found (start.ps1, watcher.ps1)." + (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
                    ? "  In use. Usage statistics: " + (tracker != null ? tracker.StatsPath : "")
                    : "  Press “Use folder” to switch to it.");
                hint.Foreground = (Brush)root.FindResource("MutedBrush");
            }
            else
            {
                hint.Text = "No " + profile.DisplayName + " install here. Run " + profile.Key + "\\install.bat from the repository, or pick the folder that has start.ps1 and watcher.ps1.";
                hint.Foreground = (Brush)root.FindResource("WarnBrush");
            }
        }

        private void BrowseFolder()
        {
            using (System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Pick the " + profile.Title + " install folder (contains start.ps1 and watcher.ps1)";
                dlg.SelectedPath = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) F<TextBox>("FolderBox").Text = dlg.SelectedPath;
            }
        }

        private void ApplyFolder()
        {
            string path = F<TextBox>("FolderBox").Text.Trim();
            if (!PlatformLocator.IsInstallFolder(path))
            {
                MessageBox.Show(this, "This folder does not contain start.ps1 and watcher.ps1.", "Install folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.Equals(path, PlatformLocator.Detect(profile), StringComparison.OrdinalIgnoreCase)) prefs.Folders.Remove(profile.Key);
            else prefs.Folders[profile.Key] = path;
            SwitchPlatform(profile);
        }

        private void UpdateClashStatus()
        {
            int port = PlatformLocator.ReadClashPort(profile, folder);
            TextBlock t = F<TextBlock>("ClashStatus");
            Button enable = F<Button>("EnableClashBtn");
            if (port > 0)
            {
                bool live = last != null && last.ClashOk;
                t.Text = "Exact tracking is enabled in " + profile.SingboxConfig + " (Clash API on 127.0.0.1:" + port + "). " +
                    (live ? "Connected — the board and the connections list show exact bytes per app, TCP and UDP."
                          : "Not reachable yet: restart the tunnel so sing-box loads it.");
                enable.Visibility = Visibility.Collapsed;
            }
            else
            {
                t.Text = "Per-app numbers are a rough estimate now: the measured total is split by how many tunnel connections each app has open (TCP only). " +
                    "Windows does not expose per-app network bytes without administrator rights. " +
                    "Exact tracking turns on sing-box's local Clash API (127.0.0.1:" + profile.DefaultClashPort + ") and process detection in " +
                    profile.SingboxConfig + ". A backup (.bak) is kept. It takes effect after a restart, which drops the connection for a few seconds.";
                enable.Visibility = Visibility.Visible;
            }
        }

        private void EnableClash()
        {
            string path = Path.Combine(folder, profile.SingboxConfig);
            Dictionary<string, object> cfg = Json.ReadFile(path);
            if (cfg == null)
            {
                MessageBox.Show(this, "Could not read " + path, "Exact tracking", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            try
            {
                Dictionary<string, object> exp = Json.Obj(cfg, "experimental");
                if (exp == null)
                {
                    exp = new Dictionary<string, object>();
                    cfg["experimental"] = exp;
                }
                Dictionary<string, object> api = new Dictionary<string, object>();
                api["external_controller"] = "127.0.0.1:" + profile.DefaultClashPort;
                exp["clash_api"] = api;
                Dictionary<string, object> route = Json.Obj(cfg, "route");
                if (route != null) route["find_process"] = true;

                File.Copy(path, path + ".bak", true);
                Json.WriteFileAtomic(path, Json.Stringify(cfg));
                AppendDash("exact tracking enabled in " + path + " (backup: " + profile.SingboxConfig + ".bak)");
                collector.Refresh();
                UpdateClashStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not update the config:\n" + ex.Message, "Exact tracking", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (last != null && last.SingboxRunning)
            {
                MessageBoxResult r = MessageBox.Show(this, "Saved. Restart the tunnel now so it takes effect?\n\nThe connection drops for a few seconds.",
                    "Exact tracking", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (r == MessageBoxResult.Yes) RunElevated("start.ps1", "Restart");
            }
        }

        private void WriteLogLevel(string level)
        {
            string path = Path.Combine(folder, profile.SingboxConfig);
            Dictionary<string, object> cfg = Json.ReadFile(path);
            if (cfg == null) return;
            Dictionary<string, object> log = Json.Obj(cfg, "log");
            if (log == null)
            {
                log = new Dictionary<string, object>();
                cfg["log"] = log;
            }
            log["level"] = level;
            try
            {
                Json.WriteFileAtomic(path, Json.Stringify(cfg));
                AppendDash("sing-box log level set to " + level + " (applies after restart)");
            }
            catch (Exception ex)
            {
                AppendDash("log level change failed: " + ex.Message);
            }
        }

        private void LoadIphoneSettings()
        {
            Dictionary<string, object> s = Json.ReadFile(Path.Combine(folder, "settings.json"));
            object[] ports = Json.Arr(s, "ports");
            F<TextBox>("SetPorts").Text = ports != null ? string.Join(", ", ports.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture))) : "";
            string mode = Json.Str(s, "vlessMode") ?? "udp";
            F<RadioButton>("VlessAll").IsChecked = mode == "all";
            F<RadioButton>("VlessUdp").IsChecked = mode != "all";
            long interval = Json.Long(s, "intervalSec");
            F<TextBox>("SetInterval").Text = interval > 0 ? interval.ToString(CultureInfo.InvariantCulture) : "5";
            F<PasswordBox>("SetVless").Clear();
            F<TextBlock>("VlessHint").Text = File.Exists(Path.Combine(folder, "vless.txt"))
                ? "A VLESS link is saved in vless.txt (not shown here)."
                : "No VLESS link saved yet.";
            F<Button>("SaveIphoneBtn").IsEnabled = s != null;
        }

        private void SaveIphoneSettings()
        {
            string path = Path.Combine(folder, "settings.json");
            Dictionary<string, object> s = Json.ReadFile(path) ?? new Dictionary<string, object>();

            List<int> ports = new List<int>();
            foreach (string part in F<TextBox>("SetPorts").Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int p;
                if (!int.TryParse(part, out p) || p < 1 || p > 65535)
                {
                    MessageBox.Show(this, "“" + part + "” is not a valid port.", "iPhone settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!ports.Contains(p)) ports.Add(p);
            }
            if (ports.Count == 0)
            {
                MessageBox.Show(this, "Enter at least one port.", "iPhone settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int interval;
            if (!int.TryParse(F<TextBox>("SetInterval").Text.Trim(), out interval) || interval < 1 || interval > 600)
            {
                MessageBox.Show(this, "The check interval must be between 1 and 600 seconds.", "iPhone settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string link = F<PasswordBox>("SetVless").Password.Trim();
            if (link.Length > 0 && !link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "The VLESS link must start with vless://", "iPhone settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            s["ports"] = ports;
            s["vlessMode"] = F<RadioButton>("VlessAll").IsChecked == true ? "all" : "udp";
            s["intervalSec"] = interval;
            try
            {
                Json.WriteFileAtomic(path, Json.Stringify(s));
                if (link.Length > 0) File.WriteAllText(Path.Combine(folder, "vless.txt"), link + "\n", new UTF8Encoding(false));
                AppendDash("iPhone settings saved" + (link.Length > 0 ? " (VLESS link updated)" : ""));
                LoadIphoneSettings();
                MessageBox.Show(this, "Saved. Restart the tunnel for the watcher to pick the changes up.", "iPhone settings",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save:\n" + ex.Message, "iPhone settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------ shutdown

        private void OnClosing(object sender, CancelEventArgs e)
        {
            collector.Stop();
            logTimer.Stop();
            if (tracker != null) tracker.Save();
            SaveSelection();
            prefs.Save();
        }
    }
}
