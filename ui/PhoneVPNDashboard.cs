using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhoneVPNDashboard
{
    public class Program : Application
    {
        [STAThread]
        public static void Main()
        {
            Program app = new Program();
            DashboardWindow win = new DashboardWindow();
            app.Run(win);
        }
    }

    public class DashboardWindow : Window
    {
        private string activePlatform = "iphone";
        private long lastLogPosition = 0;
        private string repoRoot;
        private DispatcherTimer refreshTimer;

        private Border statusIndicatorDot;
        private TextBlock txtStatusBadge;
        private TextBlock txtGatewayVal;
        private TextBlock txtProcessesVal;
        private TextBlock txtUdpVal;
        private Button btnStart;
        private Button btnStop;
        private Button btnTest;
        private Button btnSwitchIphone;
        private Button btnSwitchAndroid;
        private TextBox txtLogTerminal;
        private TextBox txtSettingPorts;
        private ComboBox cmbVlessMode;
        private TextBox txtSettingInterval;
        private TextBox txtSettingVlessLink;
        private CheckBox chkAutoScroll;

        public DashboardWindow()
        {
            repoRoot = FindRepoRoot();

            Title = "Phone-Tether-Tunnel Dashboard";
            Width = 1000;
            Height = 720;
            MinWidth = 850;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));

            BuildUi();

            refreshTimer = new DispatcherTimer();
            refreshTimer.Interval = TimeSpan.FromMilliseconds(800);
            refreshTimer.Tick += RefreshTimer_Tick;
            refreshTimer.Start();

            LoadInitialSettings();
            ReadWatcherLogs(true);
            UpdateProcessStatus();
        }

        private string FindRepoRoot()
        {
            string current = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(Path.Combine(current, "iphone")) && Directory.Exists(Path.Combine(current, "android")))
                {
                    return current;
                }
                DirectoryInfo parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private void BuildUi()
        {
            Grid mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border headerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(20, 12, 20, 12)
            };

            Grid headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
            Border iconBorder = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(10),
                Background = new LinearGradientBrush(Color.FromRgb(5, 150, 105), Color.FromRgb(6, 182, 212), 45.0),
                Margin = new Thickness(0, 0, 14, 0)
            };
            TextBlock iconTxt = new TextBlock
            {
                Text = "VPN",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = iconTxt;
            titlePanel.Children.Add(iconBorder);

            StackPanel namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock txtTitle = new TextBlock
            {
                Text = "Phone-Tether-Tunnel Desktop",
                Foreground = Brushes.White,
                FontSize = 17,
                FontWeight = FontWeights.Bold
            };
            TextBlock txtSubtitle = new TextBlock
            {
                Text = "VLESS Hotspot & USB Tunnel Controller & Routing Inspector",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 12
            };
            namePanel.Children.Add(txtTitle);
            namePanel.Children.Add(txtSubtitle);
            titlePanel.Children.Add(namePanel);
            headerGrid.Children.Add(titlePanel);
            Grid.SetColumn(titlePanel, 0);

            StackPanel headerRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            StackPanel modeTogglePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 0) };
            btnSwitchIphone = new Button
            {
                Content = "iPhone (Shadowrocket)",
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnSwitchIphone.Click += (s, e) => SwitchPlatform("iphone");

            btnSwitchAndroid = new Button
            {
                Content = "Android (Exclave)",
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                FontWeight = FontWeights.Normal,
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnSwitchAndroid.Click += (s, e) => SwitchPlatform("android");

            modeTogglePanel.Children.Add(btnSwitchIphone);
            modeTogglePanel.Children.Add(btnSwitchAndroid);
            headerRight.Children.Add(modeTogglePanel);

            btnStart = new Button
            {
                Content = "Start Tunnel",
                Padding = new Thickness(16, 7, 16, 7),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 8, 0),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnStart.Click += BtnStart_Click;
            headerRight.Children.Add(btnStart);

            btnStop = new Button
            {
                Content = "Stop Tunnel",
                Padding = new Thickness(16, 7, 16, 7),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 8, 0),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnStop.Click += BtnStop_Click;
            headerRight.Children.Add(btnStop);

            btnTest = new Button
            {
                Content = "Run Diagnostic",
                Padding = new Thickness(14, 7, 14, 7),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnTest.Click += BtnTest_Click;
            headerRight.Children.Add(btnTest);

            headerGrid.Children.Add(headerRight);
            Grid.SetColumn(headerRight, 1);
            headerBorder.Child = headerGrid;
            mainGrid.Children.Add(headerBorder);
            Grid.SetRow(headerBorder, 0);

            Grid kpiGrid = new Grid { Margin = new Thickness(20, 15, 20, 10) };
            kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            kpiGrid.Children.Add(CreateKpiCardWithDot("System Status", out txtStatusBadge, out statusIndicatorDot, 0));
            kpiGrid.Children.Add(CreateKpiCard("Phone Gateway", out txtGatewayVal, 1));
            kpiGrid.Children.Add(CreateKpiCard("Active Cores", out txtProcessesVal, 2));
            kpiGrid.Children.Add(CreateKpiCard("Discord Voice / UDP", out txtUdpVal, 3));

            mainGrid.Children.Add(kpiGrid);
            Grid.SetRow(kpiGrid, 1);

            TabControl tabs = new TabControl
            {
                Margin = new Thickness(20, 0, 20, 15),
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85))
            };

            tabs.Items.Add(CreateLogsTab());
            tabs.Items.Add(CreateRoutingInspectorTab());
            tabs.Items.Add(CreateSettingsTab());

            mainGrid.Children.Add(tabs);
            Grid.SetRow(tabs, 2);

            Content = mainGrid;
        }

        private Border CreateKpiCardWithDot(string title, out TextBlock valBlock, out Border dotIndicator, int col)
        {
            Border card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15, 10, 15, 10),
                Margin = new Thickness(col == 0 ? 0 : 6, 0, col == 3 ? 0 : 6, 0)
            };

            StackPanel sp = new StackPanel();
            TextBlock lblTitle = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 4)
            };
            sp.Children.Add(lblTitle);

            StackPanel valPanel = new StackPanel { Orientation = Orientation.Horizontal };
            dotIndicator = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            valPanel.Children.Add(dotIndicator);

            valBlock = new TextBlock
            {
                Text = "Checking...",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold
            };
            valPanel.Children.Add(valBlock);
            sp.Children.Add(valPanel);

            card.Child = sp;
            Grid.SetColumn(card, col);
            return card;
        }

        private Border CreateKpiCard(string title, out TextBlock valBlock, int col)
        {
            Border card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15, 10, 15, 10),
                Margin = new Thickness(col == 0 ? 0 : 6, 0, col == 3 ? 0 : 6, 0)
            };

            StackPanel sp = new StackPanel();
            TextBlock lblTitle = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 4)
            };
            sp.Children.Add(lblTitle);

            StackPanel valPanel = new StackPanel { Orientation = Orientation.Horizontal };
            valBlock = new TextBlock
            {
                Text = "Checking...",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold
            };
            valPanel.Children.Add(valBlock);
            sp.Children.Add(valPanel);

            card.Child = sp;
            Grid.SetColumn(card, col);
            return card;
        }

        private TabItem CreateLogsTab()
        {
            TabItem tab = new TabItem
            {
                Header = "Real-Time Watcher Logs",
                Foreground = Brushes.White,
                FontSize = 12
            };

            Grid g = new Grid { Margin = new Thickness(10) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel toolBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Right
            };

            chkAutoScroll = new CheckBox
            {
                Content = "Auto-scroll to bottom",
                IsChecked = true,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0)
            };
            toolBar.Children.Add(chkAutoScroll);

            Button btnClear = new Button
            {
                Content = "Clear Console",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnClear.Click += (s, e) => { txtLogTerminal.Clear(); lastLogPosition = 0; };
            toolBar.Children.Add(btnClear);

            g.Children.Add(toolBar);
            Grid.SetRow(toolBar, 0);

            txtLogTerminal = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1)
            };

            g.Children.Add(txtLogTerminal);
            Grid.SetRow(txtLogTerminal, 1);

            tab.Content = g;
            return tab;
        }

        private TabItem CreateRoutingInspectorTab()
        {
            TabItem tab = new TabItem
            {
                Header = "Traffic Routing Inspector (What Traverses Where)",
                Foreground = Brushes.White,
                FontSize = 12
            };

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel rootPanel = new StackPanel { Margin = new Thickness(15) };

            Border flowCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 15)
            };

            StackPanel flowContent = new StackPanel();
            TextBlock flowHeader = new TextBlock
            {
                Text = "Visual End-to-End Tunnel Pipeline",
                Foreground = new SolidColorBrush(Color.FromRgb(6, 182, 212)),
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            };
            flowContent.Children.Add(flowHeader);

            TextBlock flowDiagram = new TextBlock
            {
                Text = "Windows Apps (All traffic) ──> sing-box TUN (Wintun 172.19.0.1)\n" +
                       "                           │\n" +
                       "                           ▼ [Local SOCKS Bridge :17890 / :17880]\n" +
                       "                     Xray-core Relay (UDP wrapped in xudp)\n" +
                       "                           │\n" +
                       "                           ▼ [Hotspot / USB :1082 / :2080]\n" +
                       "                 Phone Gateway (Shadowrocket / Exclave)\n" +
                       "                           │\n" +
                       "                           ▼ [Encrypted VLESS Pipeline]\n" +
                       "                 Remote VLESS Server ──> Internet",
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                LineHeight = 18
            };
            flowContent.Children.Add(flowDiagram);
            flowCard.Child = flowContent;
            rootPanel.Children.Add(flowCard);

            TextBlock tableHeader = new TextBlock
            {
                Text = "Protocol-by-Protocol Breakdown",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 5, 0, 10)
            };
            rootPanel.Children.Add(tableHeader);

            rootPanel.Children.Add(CreateRoutingItem(
                "Web & Browser Traffic (TCP 80/443)",
                "sing-box TUN -> Xray-core -> Phone Proxy Share (:1082 / :2080)",
                "Full wire-speed TCP delivery. Handled natively by the phone proxy share without overhead."
            ));

            rootPanel.Children.Add(CreateRoutingItem(
                "Discord Voice & WebRTC (UDP 50000+)",
                "sing-box TUN -> Xray (VLESS Outbound with xudp inside TCP dialerProxy) -> Phone Share",
                "Phone proxy share drops raw UDP datagrams. Xray wraps UDP in xudp inside a TCP stream, bypassing the phone UDP block."
            ));

            rootPanel.Children.Add(CreateRoutingItem(
                "DNS Resolutions (UDP 53)",
                "sing-box Local DNS Hijack -> Remote Encrypted Proxy Resolution",
                "Completely prevents ISP DNS poisoning, domain filters, and zero data leaks."
            ));

            rootPanel.Children.Add(CreateRoutingItem(
                "Leak Killer (SetTcpEntry DELETE_TCB)",
                "Physical Network Interface -> Force Reset by watcher.ps1",
                "Terminates legacy TCP connections lingering on the physical Wi-Fi/USB adapter during reconnects to safeguard mobile data."
            ));

            scroll.Content = rootPanel;
            tab.Content = scroll;
            return tab;
        }

        private Border CreateRoutingItem(string title, string path, string reason)
        {
            Border b = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            StackPanel sp = new StackPanel();
            TextBlock t = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                FontWeight = FontWeights.Bold,
                FontSize = 12
            };
            TextBlock p = new TextBlock
            {
                Text = "Path: " + path,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 3)
            };
            TextBlock r = new TextBlock
            {
                Text = reason,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 11
            };

            sp.Children.Add(t);
            sp.Children.Add(p);
            sp.Children.Add(r);
            b.Child = sp;
            return b;
        }

        private TabItem CreateSettingsTab()
        {
            TabItem tab = new TabItem
            {
                Header = "Settings & Configuration",
                Foreground = Brushes.White,
                FontSize = 12
            };

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel sp = new StackPanel { Margin = new Thickness(20) };

            sp.Children.Add(new TextBlock
            {
                Text = "Probe Ports (settings.json)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            });
            txtSettingPorts = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 0, 0, 15)
            };
            sp.Children.Add(txtSettingPorts);

            sp.Children.Add(new TextBlock
            {
                Text = "VLESS Mode (vlessMode: 'udp' for Discord only, 'all' for entire traffic)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            });
            cmbVlessMode = new ComboBox
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = Brushes.Black,
                Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 0, 15)
            };
            cmbVlessMode.Items.Add("udp");
            cmbVlessMode.Items.Add("all");
            cmbVlessMode.SelectedIndex = 0;
            sp.Children.Add(cmbVlessMode);

            sp.Children.Add(new TextBlock
            {
                Text = "Loop Interval Seconds (intervalSec)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            });
            txtSettingInterval = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 0, 0, 15)
            };
            sp.Children.Add(txtSettingInterval);

            sp.Children.Add(new TextBlock
            {
                Text = "VLESS Link (vless.txt)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            });
            txtSettingVlessLink = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 0, 0, 20)
            };
            sp.Children.Add(txtSettingVlessLink);

            Button btnSave = new Button
            {
                Content = "Save Configuration",
                Padding = new Thickness(16, 8, 16, 8),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnSave.Click += BtnSave_Click;
            sp.Children.Add(btnSave);

            scroll.Content = sp;
            tab.Content = scroll;
            return tab;
        }

        private void SwitchPlatform(string platform)
        {
            activePlatform = platform;
            if (platform == "iphone")
            {
                btnSwitchIphone.Background = new SolidColorBrush(Color.FromRgb(51, 65, 85));
                btnSwitchIphone.Foreground = Brushes.White;
                btnSwitchAndroid.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                btnSwitchAndroid.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            }
            else
            {
                btnSwitchAndroid.Background = new SolidColorBrush(Color.FromRgb(51, 65, 85));
                btnSwitchAndroid.Foreground = Brushes.White;
                btnSwitchIphone.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                btnSwitchIphone.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            }

            lastLogPosition = 0;
            txtLogTerminal.AppendText(string.Format("\n[UI] Switched active platform to {0}\n", platform.ToUpper()));
            LoadInitialSettings();
            ReadWatcherLogs(true);
            UpdateProcessStatus();
        }

        private void LoadInitialSettings()
        {
            try
            {
                string settingsPath = Path.Combine(repoRoot, activePlatform, "app", "settings.json");
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    if (json.Contains("\"ports\""))
                    {
                        int p1 = json.IndexOf("\"ports\"");
                        int b1 = json.IndexOf("[", p1);
                        int b2 = json.IndexOf("]", b1);
                        if (b1 > 0 && b2 > b1)
                        {
                            txtSettingPorts.Text = json.Substring(b1 + 1, b2 - b1 - 1).Replace(" ", "");
                        }
                    }
                    if (json.Contains("\"intervalSec\""))
                    {
                        int p1 = json.IndexOf("\"intervalSec\"");
                        int colon = json.IndexOf(":", p1);
                        int comma = json.IndexOfAny(new char[] { ',', '}', '\r', '\n' }, colon);
                        if (colon > 0 && comma > colon)
                        {
                            txtSettingInterval.Text = json.Substring(colon + 1, comma - colon - 1).Trim();
                        }
                    }
                }

                string vlessPath = Path.Combine(repoRoot, activePlatform, "app", "vless.txt");
                if (File.Exists(vlessPath))
                {
                    txtSettingVlessLink.Text = File.ReadAllText(vlessPath).Trim();
                }
                else
                {
                    txtSettingVlessLink.Text = "";
                }
            }
            catch (Exception ex)
            {
                txtLogTerminal.AppendText(string.Format("[ERROR] Failed to load settings: {0}\n", ex.Message));
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string settingsPath = Path.Combine(repoRoot, activePlatform, "app", "settings.json");
                if (File.Exists(settingsPath))
                {
                    string portsStr = txtSettingPorts.Text.Trim();
                    string intervalStr = txtSettingInterval.Text.Trim();
                    string modeStr = cmbVlessMode.SelectedItem != null ? cmbVlessMode.SelectedItem.ToString() : "udp";

                    string newJson = string.Format("{{\n  \"ports\": [{0}],\n  \"username\": \"\",\n  \"password\": \"\",\n  \"vlessMode\": \"{1}\",\n  \"intervalSec\": {2}\n}}\n",
                        portsStr, modeStr, intervalStr);

                    File.WriteAllText(settingsPath, newJson);
                }

                string vlessPath = Path.Combine(repoRoot, activePlatform, "app", "vless.txt");
                if (!string.IsNullOrEmpty(txtSettingVlessLink.Text.Trim()))
                {
                    File.WriteAllText(vlessPath, txtSettingVlessLink.Text.Trim());
                }

                txtLogTerminal.AppendText("\n[SETTINGS] Configuration saved successfully.\n");
            }
            catch (Exception ex)
            {
                txtLogTerminal.AppendText(string.Format("\n[ERROR] Failed to save settings: {0}\n", ex.Message));
            }
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            ReadWatcherLogs(false);
            UpdateProcessStatus();
        }

        private void ReadWatcherLogs(bool readTailOnly)
        {
            try
            {
                string logFile = Path.Combine(repoRoot, activePlatform, "app", "watcher.log");
                if (!File.Exists(logFile)) return;

                FileInfo fi = new FileInfo(logFile);
                if (fi.Length < lastLogPosition)
                {
                    lastLogPosition = 0;
                }

                if (fi.Length == lastLogPosition) return;

                using (FileStream fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (readTailOnly && lastLogPosition == 0 && fs.Length > 8192)
                    {
                        fs.Seek(-8192, SeekOrigin.End);
                    }
                    else
                    {
                        fs.Seek(lastLogPosition, SeekOrigin.Begin);
                    }

                    using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        string newContent = reader.ReadToEnd();
                        lastLogPosition = fs.Position;

                        if (!string.IsNullOrEmpty(newContent))
                        {
                            txtLogTerminal.AppendText(newContent);
                            if (chkAutoScroll.IsChecked == true)
                            {
                                txtLogTerminal.ScrollToEnd();
                            }
                        }
                    }
                }
            }
            catch {}
        }

        private void UpdateProcessStatus()
        {
            bool sbRunning = false;
            bool xrRunning = false;

            Process[] procs = Process.GetProcesses();
            foreach (Process p in procs)
            {
                string n = p.ProcessName.ToLower();
                if (n.Contains("singbox") || n.Contains("sing-box")) sbRunning = true;
                if (n.Contains("xray")) xrRunning = true;
            }

            if (sbRunning && xrRunning)
            {
                txtStatusBadge.Text = "TUNNEL CONNECTED (ONLINE)";
                statusIndicatorDot.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                txtProcessesVal.Text = "sing-box & Xray (OK)";
                txtUdpVal.Text = "Active (xudp Relay)";
                txtGatewayVal.Text = activePlatform == "iphone" ? "Shadowrocket :1082" : "Exclave :2080";
            }
            else if (sbRunning || xrRunning)
            {
                txtStatusBadge.Text = "STARTING / PARTIAL";
                statusIndicatorDot.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                txtProcessesVal.Text = sbRunning ? "sing-box only" : "Xray only";
            }
            else
            {
                txtStatusBadge.Text = "TUNNEL STOPPED (OFF)";
                statusIndicatorDot.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                txtProcessesVal.Text = "Stopped";
                txtUdpVal.Text = "Inactive";
                txtGatewayVal.Text = "Not connected";
            }
        }

        private void RunScriptAsync(string scriptName)
        {
            string scriptPath = Path.Combine(repoRoot, activePlatform, "app", scriptName);
            if (!File.Exists(scriptPath))
            {
                txtLogTerminal.AppendText(string.Format("\n[ERROR] Script not found: {0}\n", scriptPath));
                return;
            }

            txtLogTerminal.AppendText(string.Format("\n[EXEC] Executing {0} in background...\n", scriptName));

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\"", scriptPath),
                WorkingDirectory = Path.Combine(repoRoot, activePlatform, "app"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (s, ev) =>
            {
                if (!string.IsNullOrEmpty(ev.Data))
                {
                    Dispatcher.Invoke(() =>
                    {
                        txtLogTerminal.AppendText(ev.Data + "\n");
                        if (chkAutoScroll.IsChecked == true) txtLogTerminal.ScrollToEnd();
                    });
                }
            };
            proc.ErrorDataReceived += (s, ev) =>
            {
                if (!string.IsNullOrEmpty(ev.Data))
                {
                    Dispatcher.Invoke(() =>
                    {
                        txtLogTerminal.AppendText("[ERR] " + ev.Data + "\n");
                        if (chkAutoScroll.IsChecked == true) txtLogTerminal.ScrollToEnd();
                    });
                }
            };

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                txtLogTerminal.AppendText(string.Format("\n[ERROR] Process launch failed: {0}\n", ex.Message));
            }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            RunScriptAsync("start.ps1");
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            RunScriptAsync("stop.ps1");
        }

        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            RunScriptAsync("test.ps1");
        }
    }
}
