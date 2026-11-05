using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
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

    public class AppConnectionItem
    {
        public string ProcessName { get; set; }
        public string Protocol { get; set; }
        public string LocalEndpoint { get; set; }
        public string RemoteEndpoint { get; set; }
        public string RouteType { get; set; }
        public string Status { get; set; }
    }

    public class DashboardWindow : Window
    {
        private string activePlatform = "iphone";
        private string activeFolder = "";
        private long lastWatcherLogPos = 0;
        private long lastSingboxLogPos = 0;
        private DispatcherTimer refreshTimer;
        private DispatcherTimer connectionsTimer;

        private Border statusIndicatorDot;
        private TextBlock txtStatusBadge;
        private TextBlock txtGatewayVal;
        private TextBlock txtProcessesVal;
        private TextBlock txtActiveFolderVal;
        private TextBox txtCustomFolderPath;

        private Button btnStart;
        private Button btnStop;
        private Button btnTest;
        private Button btnSwitchIphone;
        private Button btnSwitchAndroid;

        private Button navLiveFeedBtn;
        private Button navLogsBtn;
        private Button navRoutingBtn;
        private Button navSettingsBtn;

        private Grid pageLiveFeed;
        private Grid pageLogs;
        private Grid pageRouting;
        private Grid pageSettings;

        private ListView listConnections;
        private TextBox txtLogTerminal;
        private ComboBox cmbLogSource;
        private CheckBox chkAutoScroll;

        private TextBox txtSettingPorts;
        private ComboBox cmbVlessMode;
        private ComboBox cmbSingboxLogLevel;
        private TextBox txtSettingInterval;
        private TextBox txtSettingVlessLink;

        public DashboardWindow()
        {
            activeFolder = AutoDetectActiveFolder();

            Title = "Phone-Tether-Tunnel Live Monitor & Dashboard";
            Width = 1150;
            Height = 820;
            MinWidth = 950;
            MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));

            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            UseLayoutRounding = true;

            BuildUi();

            refreshTimer = new DispatcherTimer();
            refreshTimer.Interval = TimeSpan.FromMilliseconds(800);
            refreshTimer.Tick += RefreshTimer_Tick;
            refreshTimer.Start();

            connectionsTimer = new DispatcherTimer();
            connectionsTimer.Interval = TimeSpan.FromSeconds(2);
            connectionsTimer.Tick += ConnectionsTimer_Tick;
            connectionsTimer.Start();

            LoadInitialSettings();
            ReadLogs(true);
            UpdateProcessStatus();
            UpdateLiveConnections();
        }

        private string AutoDetectActiveFolder()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appDataPhoneVpn = Path.Combine(localAppData, "PhoneVPN");
            if (Directory.Exists(appDataPhoneVpn) && File.Exists(Path.Combine(appDataPhoneVpn, "settings.json")))
            {
                return appDataPhoneVpn;
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string iphoneApp = Path.Combine(userProfile, "PhoneVPN-iPhone", "app");
            if (Directory.Exists(iphoneApp)) return iphoneApp;

            string androidApp = Path.Combine(userProfile, "PhoneVPN-Android", "app");
            if (Directory.Exists(androidApp)) return androidApp;

            string downloadsIphone = Path.Combine(userProfile, "Downloads", "AyuGram Desktop", "PhoneVPN-iPhone", "app");
            if (Directory.Exists(downloadsIphone)) return downloadsIphone;

            string current = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(Path.Combine(current, "iphone", "app")))
                {
                    return Path.Combine(current, "iphone", "app");
                }
                DirectoryInfo parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private void BuildUi()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(108) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border headerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(24, 10, 24, 10)
            };

            Grid headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Border iconBorder = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(12),
                Background = new LinearGradientBrush(Color.FromRgb(16, 185, 129), Color.FromRgb(6, 182, 212), 45.0),
                Margin = new Thickness(0, 0, 14, 0)
            };
            TextBlock iconTxt = new TextBlock
            {
                Text = "VPN",
                Foreground = Brushes.White,
                FontWeight = FontWeights.ExtraBold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = iconTxt;
            titlePanel.Children.Add(iconBorder);

            StackPanel namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock txtTitle = new TextBlock
            {
                Text = "Phone-Tether-Tunnel Live Monitor",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.Bold
            };

            StackPanel folderPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            TextBlock lblFolder = new TextBlock
            {
                Text = "Active Directory: ",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            txtActiveFolderVal = new TextBlock
            {
                Text = activeFolder,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas")
            };
            folderPanel.Children.Add(lblFolder);
            folderPanel.Children.Add(txtActiveFolderVal);

            namePanel.Children.Add(txtTitle);
            namePanel.Children.Add(folderPanel);
            titlePanel.Children.Add(namePanel);
            headerGrid.Children.Add(titlePanel);
            Grid.SetColumn(titlePanel, 0);

            StackPanel headerRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            StackPanel modeTogglePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            btnSwitchIphone = new Button
            {
                Content = "iPhone (Shadowrocket)",
                Padding = new Thickness(14, 8, 14, 8),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnSwitchIphone.Click += (s, e) => SwitchPlatform("iphone");

            btnSwitchAndroid = new Button
            {
                Content = "Android (Exclave)",
                Padding = new Thickness(14, 8, 14, 8),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnSwitchAndroid.Click += (s, e) => SwitchPlatform("android");

            modeTogglePanel.Children.Add(btnSwitchIphone);
            modeTogglePanel.Children.Add(btnSwitchAndroid);
            headerRight.Children.Add(modeTogglePanel);

            btnStart = new Button
            {
                Content = "Start Tunnel",
                Padding = new Thickness(18, 8, 18, 8),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(22, 163, 74)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnStart.Click += BtnStart_Click;
            headerRight.Children.Add(btnStart);

            btnStop = new Button
            {
                Content = "Stop Tunnel",
                Padding = new Thickness(18, 8, 18, 8),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnStop.Click += BtnStop_Click;
            headerRight.Children.Add(btnStop);

            btnTest = new Button
            {
                Content = "Run Diagnostic",
                Padding = new Thickness(16, 8, 16, 8),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnTest.Click += BtnTest_Click;
            headerRight.Children.Add(btnTest);

            headerGrid.Children.Add(headerRight);
            Grid.SetColumn(headerRight, 1);
            headerBorder.Child = headerGrid;
            root.Children.Add(headerBorder);
            Grid.SetRow(headerBorder, 0);

            Grid kpiGrid = new Grid { Margin = new Thickness(24, 12, 24, 8) };
            kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            kpiGrid.Children.Add(CreateKpiCardWithDot("SYSTEM STATUS", out txtStatusBadge, out statusIndicatorDot, 0));
            kpiGrid.Children.Add(CreateKpiCard("PHONE GATEWAY", out txtGatewayVal, 1, Color.FromRgb(56, 189, 248)));
            kpiGrid.Children.Add(CreateKpiCard("ACTIVE CORES", out txtProcessesVal, 2, Color.FromRgb(250, 204, 21)));
            TextBlock unusedTxt;
            kpiGrid.Children.Add(CreateKpiCard("LOCATION", out unusedTxt, 3, Color.FromRgb(192, 132, 252), "Frankfurt (VLESS)"));

            root.Children.Add(kpiGrid);
            Grid.SetRow(kpiGrid, 1);

            StackPanel navBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(24, 4, 24, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            navLiveFeedBtn = CreateNavTabButton("Live App Traffic (Hangi Uygulama Nereye Bagli)", true);
            navLiveFeedBtn.Click += (s, e) => SwitchTab("livefeed");
            navBar.Children.Add(navLiveFeedBtn);

            navLogsBtn = CreateNavTabButton("Real-Time Logs (Anlik Loglar)", false);
            navLogsBtn.Click += (s, e) => SwitchTab("logs");
            navBar.Children.Add(navLogsBtn);

            navRoutingBtn = CreateNavTabButton("Traffic Routing Inspector (Nelerin Nelerden Gectigi)", false);
            navRoutingBtn.Click += (s, e) => SwitchTab("routing");
            navBar.Children.Add(navRoutingBtn);

            navSettingsBtn = CreateNavTabButton("Configuration & Directory (Ayarlar / Klasor)", false);
            navSettingsBtn.Click += (s, e) => SwitchTab("settings");
            navBar.Children.Add(navSettingsBtn);

            root.Children.Add(navBar);
            Grid.SetRow(navBar, 2);

            Grid contentArea = new Grid { Margin = new Thickness(24, 4, 24, 18) };

            pageLiveFeed = CreateLiveFeedPage();
            pageLogs = CreateLogsPage();
            pageRouting = CreateRoutingInspectorPage();
            pageSettings = CreateSettingsPage();

            contentArea.Children.Add(pageLiveFeed);
            contentArea.Children.Add(pageLogs);
            contentArea.Children.Add(pageRouting);
            contentArea.Children.Add(pageSettings);

            pageLogs.Visibility = Visibility.Collapsed;
            pageRouting.Visibility = Visibility.Collapsed;
            pageSettings.Visibility = Visibility.Collapsed;

            root.Children.Add(contentArea);
            Grid.SetRow(contentArea, 3);

            Content = root;
        }

        private Button CreateNavTabButton(string text, bool isActive)
        {
            Button btn = new Button
            {
                Content = text,
                Padding = new Thickness(16, 9, 16, 9),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(1, 1, 1, 0)
            };

            if (isActive)
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                btn.Foreground = Brushes.White;
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
            }
            else
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));
                btn.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));
            }

            return btn;
        }

        private void SwitchTab(string tabName)
        {
            navLiveFeedBtn.Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));
            navLiveFeedBtn.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            navLiveFeedBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));

            navLogsBtn.Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));
            navLogsBtn.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            navLogsBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));

            navRoutingBtn.Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));
            navRoutingBtn.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            navRoutingBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));

            navSettingsBtn.Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));
            navSettingsBtn.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            navSettingsBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));

            pageLiveFeed.Visibility = Visibility.Collapsed;
            pageLogs.Visibility = Visibility.Collapsed;
            pageRouting.Visibility = Visibility.Collapsed;
            pageSettings.Visibility = Visibility.Collapsed;

            if (tabName == "livefeed")
            {
                navLiveFeedBtn.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                navLiveFeedBtn.Foreground = Brushes.White;
                navLiveFeedBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                pageLiveFeed.Visibility = Visibility.Visible;
                UpdateLiveConnections();
            }
            else if (tabName == "logs")
            {
                navLogsBtn.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                navLogsBtn.Foreground = Brushes.White;
                navLogsBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                pageLogs.Visibility = Visibility.Visible;
            }
            else if (tabName == "routing")
            {
                navRoutingBtn.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                navRoutingBtn.Foreground = Brushes.White;
                navRoutingBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                pageRouting.Visibility = Visibility.Visible;
            }
            else if (tabName == "settings")
            {
                navSettingsBtn.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                navSettingsBtn.Foreground = Brushes.White;
                navSettingsBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                pageSettings.Visibility = Visibility.Visible;
            }
        }

        private Border CreateKpiCardWithDot(string title, out TextBlock valBlock, out Border dotIndicator, int col)
        {
            Border card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(col == 0 ? 0 : 6, 0, col == 3 ? 0 : 6, 0)
            };

            StackPanel sp = new StackPanel();
            TextBlock lblTitle = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(147, 197, 253)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            sp.Children.Add(lblTitle);

            StackPanel valPanel = new StackPanel { Orientation = Orientation.Horizontal };
            dotIndicator = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            valPanel.Children.Add(dotIndicator);

            valBlock = new TextBlock
            {
                Text = "CHECKING...",
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.ExtraBold
            };
            valPanel.Children.Add(valBlock);
            sp.Children.Add(valPanel);

            card.Child = sp;
            Grid.SetColumn(card, col);
            return card;
        }

        private Border CreateKpiCard(string title, out TextBlock valBlock, int col, Color accentColor, string defaultText = "CHECKING...")
        {
            Border card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(col == 0 ? 0 : 6, 0, col == 3 ? 0 : 6, 0)
            };

            StackPanel sp = new StackPanel();
            TextBlock lblTitle = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(147, 197, 253)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            sp.Children.Add(lblTitle);

            valBlock = new TextBlock
            {
                Text = defaultText,
                Foreground = new SolidColorBrush(accentColor),
                FontSize = 15,
                FontWeight = FontWeights.ExtraBold
            };
            sp.Children.Add(valBlock);

            card.Child = sp;
            Grid.SetColumn(card, col);
            return card;
        }

        private Grid CreateLiveFeedPage()
        {
            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border topBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1, 1, 1, 0),
                Padding = new Thickness(16, 10, 16, 10)
            };

            Grid barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel infoPanel = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock txtLiveIcon = new TextBlock
            {
                Text = "● LIVE SOCKET MONITOR: ",
                Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
                FontWeight = FontWeights.ExtraBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock txtLiveDesc = new TextBlock
            {
                Text = "Real-time list of applications passing through the TUN adapter into Xray & Phone proxy",
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            infoPanel.Children.Add(txtLiveIcon);
            infoPanel.Children.Add(txtLiveDesc);
            barGrid.Children.Add(infoPanel);
            Grid.SetColumn(infoPanel, 0);

            Button btnRefreshConn = new Button
            {
                Content = "Refresh Now",
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnRefreshConn.Click += (s, e) => UpdateLiveConnections();
            barGrid.Children.Add(btnRefreshConn);
            Grid.SetColumn(btnRefreshConn, 1);

            topBar.Child = barGrid;
            g.Children.Add(topBar);
            Grid.SetRow(topBar, 0);

            listConnections = new ListView
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1)
            };

            GridView gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn { Header = "Application (Process)", DisplayMemberBinding = new System.Windows.Data.Binding("ProcessName"), Width = 200 });
            gridView.Columns.Add(new GridViewColumn { Header = "Proto", DisplayMemberBinding = new System.Windows.Data.Binding("Protocol"), Width = 70 });
            gridView.Columns.Add(new GridViewColumn { Header = "Local Adapter Endpoint", DisplayMemberBinding = new System.Windows.Data.Binding("LocalEndpoint"), Width = 230 });
            gridView.Columns.Add(new GridViewColumn { Header = "Remote Target (Destination)", DisplayMemberBinding = new System.Windows.Data.Binding("RemoteEndpoint"), Width = 260 });
            gridView.Columns.Add(new GridViewColumn { Header = "Tunnel Routing", DisplayMemberBinding = new System.Windows.Data.Binding("RouteType"), Width = 220 });
            gridView.Columns.Add(new GridViewColumn { Header = "State", DisplayMemberBinding = new System.Windows.Data.Binding("Status"), Width = 110 });

            listConnections.View = gridView;
            g.Children.Add(listConnections);
            Grid.SetRow(listConnections, 1);

            return g;
        }

        private Grid CreateLogsPage()
        {
            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border controlBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1, 1, 1, 0),
                Padding = new Thickness(14, 8, 14, 8)
            };

            Grid barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel leftControls = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock lblSource = new TextBlock
            {
                Text = "Log File Source: ",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            cmbLogSource = new ComboBox
            {
                Background = Brushes.White,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2)
            };
            cmbLogSource.Items.Add("watcher.log (Watcher Status & Health)");
            cmbLogSource.Items.Add("singbox.log (sing-box Inbound/Outbound Engine)");
            cmbLogSource.Items.Add("xray-error.log (Xray Core Logs)");
            cmbLogSource.SelectedIndex = 0;
            cmbLogSource.SelectionChanged += (s, e) => { lastWatcherLogPos = 0; lastSingboxLogPos = 0; txtLogTerminal.Clear(); ReadLogs(true); };

            leftControls.Children.Add(lblSource);
            leftControls.Children.Add(cmbLogSource);
            barGrid.Children.Add(leftControls);
            Grid.SetColumn(leftControls, 0);

            StackPanel rightControls = new StackPanel { Orientation = Orientation.Horizontal };
            chkAutoScroll = new CheckBox
            {
                Content = "Auto-scroll to latest output",
                IsChecked = true,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };
            rightControls.Children.Add(chkAutoScroll);

            Button btnClear = new Button
            {
                Content = "Clear Console",
                Padding = new Thickness(14, 4, 14, 4),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnClear.Click += (s, e) => { txtLogTerminal.Clear(); lastWatcherLogPos = 0; lastSingboxLogPos = 0; };
            rightControls.Children.Add(btnClear);

            barGrid.Children.Add(rightControls);
            Grid.SetColumn(rightControls, 1);
            controlBar.Child = barGrid;

            g.Children.Add(controlBar);
            Grid.SetRow(controlBar, 0);

            txtLogTerminal = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(5, 9, 20)),
                Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.Normal,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(14),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1)
            };

            g.Children.Add(txtLogTerminal);
            Grid.SetRow(txtLogTerminal, 1);
            return g;
        }

        private Grid CreateRoutingInspectorPage()
        {
            Grid g = new Grid();
            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel rootPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };

            Border flowCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18),
                Margin = new Thickness(0, 0, 0, 16)
            };

            StackPanel flowContent = new StackPanel();
            TextBlock flowHeader = new TextBlock
            {
                Text = "Visual End-to-End Tunnel Pipeline (Hangi Paket Nereden Geciyor?)",
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Margin = new Thickness(0, 0, 0, 12)
            };
            flowContent.Children.Add(flowHeader);

            Border diagramBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14)
            };

            TextBlock flowDiagram = new TextBlock
            {
                Text = "[ Windows Apps (All Traffic) ]\n" +
                       "         |\n" +
                       "         v  (Captured 100% by Wintun TUN Adapter)\n" +
                       "[ sing-box TUN : 172.19.1.1 / 172.19.0.1 ]\n" +
                       "         |\n" +
                       "         v  (Local SOCKS5 Bridge: 127.0.0.1:17890 / :17880)\n" +
                       "[ Xray-core Relay ]  --> (UDP converted into xudp inside TCP dialerProxy)\n" +
                       "         |\n" +
                       "         v  (Phone Hotspot or USB Tethering Network)\n" +
                       "[ Phone Proxy Gateway : 172.20.10.1:1082 (iPhone) or :2080 (Android) ]\n" +
                       "         |\n" +
                       "         v  (Encrypted VLESS Pipeline via Cellular Data)\n" +
                       "[ Remote VLESS Server ] ──> Global Internet Egress",
                Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                LineHeight = 20
            };
            diagramBox.Child = flowDiagram;
            flowContent.Children.Add(diagramBox);
            flowCard.Child = flowContent;
            rootPanel.Children.Add(flowCard);

            TextBlock tableHeader = new TextBlock
            {
                Text = "Protocol-by-Protocol Detailed Matrix",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Margin = new Thickness(0, 4, 0, 10)
            };
            rootPanel.Children.Add(tableHeader);

            rootPanel.Children.Add(CreateRoutingItem(
                "Web & Browser Traffic (TCP Port 80 & 443)",
                "sing-box TUN  -->  Xray-core (:17890)  -->  Shadowrocket Proxy Share (:1082)  -->  VLESS",
                "Delivered at full native TCP throughput. Handled directly by phone proxy share without packet drop."
            ));

            rootPanel.Children.Add(CreateRoutingItem(
                "Discord Voice & WebRTC Channels (UDP 50000+)",
                "sing-box TUN  -->  Xray (VLESS Outbound, xudp inside TCP dialerProxy)  -->  Proxy Share (:1082)",
                "CRITICAL: Shadowrocket drops raw UDP. Xray wraps UDP in xudp inside a TCP connection via vless.txt, guaranteeing crystal clear voice."
            ));

            rootPanel.Children.Add(CreateRoutingItem(
                "System DNS Lookups (UDP Port 53)",
                "sing-box Local DNS Intercept  -->  Resolved Remotely via VLESS Upstream",
                "Completely prevents ISP DNS poisoning, blocking, and ISP query surveillance."
            ));

            rootPanel.Children.Add(CreateRoutingItem(
                "Data Leak Guard (SetTcpEntry DELETE_TCB)",
                "Physical Wi-Fi/USB Network Adapter  -->  Force Terminated by watcher.ps1 Loop",
                "Connections created during restart gaps are terminated so apps reconnect through the encrypted TUN."
            ));

            scroll.Content = rootPanel;
            g.Children.Add(scroll);
            return g;
        }

        private Border CreateRoutingItem(string title, string path, string reason)
        {
            Border b = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 10)
            };

            StackPanel sp = new StackPanel();
            TextBlock t = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                FontWeight = FontWeights.Bold,
                FontSize = 13
            };
            TextBlock p = new TextBlock
            {
                Text = "Path: " + path,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 4)
            };
            TextBlock r = new TextBlock
            {
                Text = reason,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };

            sp.Children.Add(t);
            sp.Children.Add(p);
            sp.Children.Add(r);
            b.Child = sp;
            return b;
        }

        private Grid CreateSettingsPage()
        {
            Grid g = new Grid();
            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel sp = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };

            Border dirBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18),
                Margin = new Thickness(0, 0, 0, 16)
            };

            StackPanel dirInner = new StackPanel();
            dirInner.Children.Add(new TextBlock
            {
                Text = "Active Working Folder (Calisilan Klasor)",
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 4)
            });
            dirInner.Children.Add(new TextBlock
            {
                Text = "If you moved or copied PhoneVPN to another folder, specify the exact folder path below:",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            });

            Grid pathGrid = new Grid();
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            txtCustomFolderPath = new TextBox
            {
                Text = activeFolder,
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            };
            pathGrid.Children.Add(txtCustomFolderPath);
            Grid.SetColumn(txtCustomFolderPath, 0);

            Button btnApplyFolder = new Button
            {
                Content = "Apply Folder",
                Padding = new Thickness(16, 6, 16, 6),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)),
                Foreground = Brushes.White,
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnApplyFolder.Click += (s, e) =>
            {
                string target = txtCustomFolderPath.Text.Trim();
                if (Directory.Exists(target))
                {
                    activeFolder = target;
                    txtActiveFolderVal.Text = target;
                    lastWatcherLogPos = 0;
                    lastSingboxLogPos = 0;
                    txtLogTerminal.AppendText(string.Format("\n[FOLDER] Active directory changed to: {0}\n", target));
                    LoadInitialSettings();
                    ReadLogs(true);
                }
                else
                {
                    MessageBox.Show("Folder does not exist: " + target, "Invalid Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            pathGrid.Children.Add(btnApplyFolder);
            Grid.SetColumn(btnApplyFolder, 1);

            dirInner.Children.Add(pathGrid);
            dirBox.Child = dirInner;
            sp.Children.Add(dirBox);

            Border settingsBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20)
            };

            StackPanel inner = new StackPanel();

            inner.Children.Add(new TextBlock
            {
                Text = "Target Phone Ports (settings.json)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6)
            });
            txtSettingPorts = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 16)
            };
            inner.Children.Add(txtSettingPorts);

            inner.Children.Add(new TextBlock
            {
                Text = "VLESS Mode (vlessMode: 'udp' for Discord Voice only, 'all' for entire PC traffic)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6)
            });
            cmbVlessMode = new ComboBox
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 16)
            };
            cmbVlessMode.Items.Add("udp");
            cmbVlessMode.Items.Add("all");
            cmbVlessMode.SelectedIndex = 0;
            inner.Children.Add(cmbVlessMode);

            inner.Children.Add(new TextBlock
            {
                Text = "sing-box Log Level (Set to 'info' to log every app connection into singbox.log)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6)
            });
            cmbSingboxLogLevel = new ComboBox
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 16)
            };
            cmbSingboxLogLevel.Items.Add("info (Logs all individual connections)");
            cmbSingboxLogLevel.Items.Add("warn (Default: errors and warnings only)");
            cmbSingboxLogLevel.SelectedIndex = 0;
            inner.Children.Add(cmbSingboxLogLevel);

            inner.Children.Add(new TextBlock
            {
                Text = "Loop Check Interval Seconds (intervalSec)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6)
            });
            txtSettingInterval = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 16)
            };
            inner.Children.Add(txtSettingInterval);

            inner.Children.Add(new TextBlock
            {
                Text = "VLESS Server URI (vless.txt)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6)
            });
            txtSettingVlessLink = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 20)
            };
            inner.Children.Add(txtSettingVlessLink);

            Button btnSave = new Button
            {
                Content = "Save Settings & Apply",
                Padding = new Thickness(22, 10, 22, 10),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnSave.Click += BtnSave_Click;
            inner.Children.Add(btnSave);

            settingsBox.Child = inner;
            sp.Children.Add(settingsBox);
            scroll.Content = sp;
            g.Children.Add(scroll);
            return g;
        }

        private void SwitchPlatform(string platform)
        {
            activePlatform = platform;
            if (platform == "iphone")
            {
                btnSwitchIphone.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
                btnSwitchIphone.Foreground = Brushes.White;
                btnSwitchIphone.BorderBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250));

                btnSwitchAndroid.Background = new SolidColorBrush(Color.FromRgb(51, 65, 85));
                btnSwitchAndroid.Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225));
                btnSwitchAndroid.BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105));
            }
            else
            {
                btnSwitchAndroid.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
                btnSwitchAndroid.Foreground = Brushes.White;
                btnSwitchAndroid.BorderBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250));

                btnSwitchIphone.Background = new SolidColorBrush(Color.FromRgb(51, 65, 85));
                btnSwitchIphone.Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225));
                btnSwitchIphone.BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105));
            }

            lastWatcherLogPos = 0;
            lastSingboxLogPos = 0;
            txtLogTerminal.AppendText(string.Format("\n[PLATFORM] Switched active platform to {0}\n", platform.ToUpper()));
            LoadInitialSettings();
            ReadLogs(true);
            UpdateProcessStatus();
        }

        private void LoadInitialSettings()
        {
            try
            {
                string settingsPath = Path.Combine(activeFolder, "settings.json");
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

                string vlessPath = Path.Combine(activeFolder, "vless.txt");
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
                string settingsPath = Path.Combine(activeFolder, "settings.json");
                if (File.Exists(settingsPath))
                {
                    string portsStr = txtSettingPorts.Text.Trim();
                    string intervalStr = txtSettingInterval.Text.Trim();
                    string modeStr = cmbVlessMode.SelectedItem != null ? cmbVlessMode.SelectedItem.ToString() : "udp";

                    string newJson = string.Format("{{\n  \"ports\": [{0}],\n  \"username\": \"\",\n  \"password\": \"\",\n  \"vlessMode\": \"{1}\",\n  \"intervalSec\": {2}\n}}\n",
                        portsStr, modeStr, intervalStr);

                    File.WriteAllText(settingsPath, newJson);
                }

                string vlessPath = Path.Combine(activeFolder, "vless.txt");
                if (!string.IsNullOrEmpty(txtSettingVlessLink.Text.Trim()))
                {
                    File.WriteAllText(vlessPath, txtSettingVlessLink.Text.Trim());
                }

                string singboxCfgPath = Path.Combine(activeFolder, "singbox.json");
                if (File.Exists(singboxCfgPath) && cmbSingboxLogLevel.SelectedIndex == 0)
                {
                    string sbContent = File.ReadAllText(singboxCfgPath);
                    if (sbContent.Contains("\"level\": \"warn\""))
                    {
                        sbContent = sbContent.Replace("\"level\": \"warn\"", "\"level\": \"info\"");
                        File.WriteAllText(singboxCfgPath, sbContent);
                    }
                }

                txtLogTerminal.AppendText("\n[SETTINGS] Configuration updated successfully.\n");
            }
            catch (Exception ex)
            {
                txtLogTerminal.AppendText(string.Format("\n[ERROR] Failed to save settings: {0}\n", ex.Message));
            }
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            ReadLogs(false);
            UpdateProcessStatus();
        }

        private HashSet<string> seenConnections = new HashSet<string>();

        private void ConnectionsTimer_Tick(object sender, EventArgs e)
        {
            UpdateLiveConnections();
        }

        private void UpdateLiveConnections()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Get-NetTCPConnection -State Established,SynSent | Select-Object -First 40 | ForEach-Object { $p = (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).Name; if (-not $p) { $p = 'System' }; \"\"$p|$($_.OwningProcess)|$($_.LocalAddress):$($_.LocalPort)|$($_.RemoteAddress):$($_.RemotePort)|$($_.State)\"\" }\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                Process proc = Process.Start(psi);
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                List<AppConnectionItem> items = new List<AppConnectionItem>();
                string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                string nowTime = DateTime.Now.ToString("HH:mm:ss");

                foreach (string line in lines)
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 5)
                    {
                        string procName = parts[0];
                        string pid = parts[1];
                        string local = parts[2];
                        string remote = parts[3];
                        string state = parts[4];

                        string route = "DIRECT / LOCAL";
                        bool isProxied = false;

                        if (local.StartsWith("172.19.") || local.StartsWith("127.0.0.1:17890") || local.StartsWith("127.0.0.1:17880"))
                        {
                            route = "PROXIED (TUNNEL -> VLESS)";
                            isProxied = true;
                        }
                        else if (remote.Contains(":1082") || remote.Contains(":2080"))
                        {
                            route = "PHONE PROXY GATEWAY";
                        }

                        items.Add(new AppConnectionItem
                        {
                            ProcessName = string.Format("{0} ({1})", procName, pid),
                            Protocol = "TCP",
                            LocalEndpoint = local,
                            RemoteEndpoint = remote,
                            RouteType = route,
                            Status = state
                        });

                        if (isProxied)
                        {
                            string connKey = string.Format("{0}->{1}", procName, remote);
                            if (!seenConnections.Contains(connKey))
                            {
                                seenConnections.Add(connKey);
                                if (seenConnections.Count > 120) seenConnections.Clear();

                                string logLine = string.Format("[{0}] [TRAFFIC] {1} ({2}) -> {3} [TUNNEL PROXIED -> VLESS]\n", nowTime, procName, pid, remote);
                                txtLogTerminal.AppendText(logLine);
                                if (chkAutoScroll.IsChecked == true)
                                {
                                    txtLogTerminal.ScrollToEnd();
                                }
                            }
                        }
                    }
                }

                if (items.Count > 0 && listConnections != null)
                {
                    listConnections.ItemsSource = items;
                }
            }
            catch {}
        }

        private void ReadLogs(bool readTailOnly)
        {
            string chosenFile = "watcher.log";
            if (cmbLogSource != null && cmbLogSource.SelectedIndex == 1) chosenFile = "singbox.log";
            if (cmbLogSource != null && cmbLogSource.SelectedIndex == 2) chosenFile = "xray-error.log";

            string logPath = Path.Combine(activeFolder, chosenFile);
            if (!File.Exists(logPath)) return;

            try
            {
                FileInfo fi = new FileInfo(logPath);
                long currentPos = chosenFile == "singbox.log" ? lastSingboxLogPos : lastWatcherLogPos;

                if (fi.Length < currentPos)
                {
                    currentPos = 0;
                }

                if (fi.Length == currentPos) return;

                using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (readTailOnly && currentPos == 0 && fs.Length > 8192)
                    {
                        fs.Seek(-8192, SeekOrigin.End);
                    }
                    else
                    {
                        fs.Seek(currentPos, SeekOrigin.Begin);
                    }

                    using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        string newContent = reader.ReadToEnd();
                        long newPos = fs.Position;
                        if (chosenFile == "singbox.log") lastSingboxLogPos = newPos;
                        else lastWatcherLogPos = newPos;

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
                txtStatusBadge.Text = "ONLINE (TUNNEL ACTIVE)";
                statusIndicatorDot.Background = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                txtProcessesVal.Text = "sing-box & Xray RUNNING";
                txtGatewayVal.Text = activePlatform == "iphone" ? "Hotspot :1082" : "Exclave :2080";
            }
            else if (sbRunning || xrRunning)
            {
                txtStatusBadge.Text = "PARTIAL / STARTING";
                statusIndicatorDot.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                txtProcessesVal.Text = sbRunning ? "sing-box only" : "Xray only";
            }
            else
            {
                txtStatusBadge.Text = "STOPPED (OFF)";
                statusIndicatorDot.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                txtProcessesVal.Text = "STOPPED";
                txtGatewayVal.Text = "DISCONNECTED";
            }
        }

        private void RunScriptAsync(string scriptName)
        {
            string scriptPath = Path.Combine(activeFolder, scriptName);
            if (!File.Exists(scriptPath))
            {
                txtLogTerminal.AppendText(string.Format("\n[ERROR] Script not found: {0}\n", scriptPath));
                return;
            }

            txtLogTerminal.AppendText(string.Format("\n[EXEC] Running {0} in {1}...\n", scriptName, activeFolder));

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\"", scriptPath),
                WorkingDirectory = activeFolder,
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
                txtLogTerminal.AppendText(string.Format("\n[ERROR] Launch failed: {0}\n", ex.Message));
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
