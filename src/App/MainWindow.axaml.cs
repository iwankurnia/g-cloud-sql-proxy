using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CloudSqlProxyManager
{
    public partial class MainWindow : Window
    {
        private const string AppTitle = "Cloud SQL Proxy Manager";
        private const string DownloadUrl = "https://github.com/GoogleCloudPlatform/cloud-sql-proxy/releases";

        private static readonly string[] Regions = new string[]
        {
            "asia-southeast2", "asia-southeast1", "asia-east1", "asia-east2", "asia-northeast1",
            "asia-northeast2", "asia-northeast3", "asia-south1", "asia-south2",
            "australia-southeast1", "australia-southeast2", "africa-south1",
            "europe-central2", "europe-north1", "europe-southwest1", "europe-west1", "europe-west2",
            "europe-west3", "europe-west4", "europe-west6", "europe-west8", "europe-west9",
            "me-central1", "me-west1", "northamerica-northeast1", "northamerica-northeast2",
            "southamerica-east1", "southamerica-west1", "us-central1", "us-east1", "us-east4",
            "us-east5", "us-south1", "us-west1", "us-west2", "us-west3", "us-west4"
        };

        private readonly ProxyController _controller = new ProxyController();
        private readonly WindowIcon[] _icons = new WindowIcon[4];
        private readonly DispatcherTimer _portTimer = new DispatcherTimer();
        private readonly DispatcherTimer _wakeTimer = new DispatcherTimer();

        private AppSettings _settings;
        private ProxyState _state = ProxyState.Disconnected;
        private bool _loading;
        private bool _advancedOpen;
        private bool _closing;
        private bool _confirmedClose;
        private string _activeInstance;
        private int _activePort;
        private int _suggestedPort;
        private int _portHintSerial;
        private DateTime _lastTick = DateTime.UtcNow;
        private TrayIcon _tray;
        private NativeMenuItem _trayConnect;
        private NativeMenuItem _trayDisconnect;

        public MainWindow()
        {
            InitializeComponent();
            LoadIcons();
            Icon = _icons[0];
            BuildTray();

            _settings = SettingsStore.Load();
            LoadSettingsIntoUi();
            WireEvents();

            _controller.StateChanged += (state, detail) => Dispatcher.UIThread.Post(() => ApplyState(state, detail));
            _controller.Log += line => Dispatcher.UIThread.Post(() => AppendLog(line));
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

            // Keeps the "port free / in use" hint current while idle, e.g. when a
            // second copy of this app connects on the same port.
            _portTimer.Interval = TimeSpan.FromSeconds(2);
            _portTimer.Tick += delegate { UpdatePortHint(); };
            _portTimer.Start();

            // Sleep/wake has no portable event; a jump in the clock is the tell.
            _wakeTimer.Interval = TimeSpan.FromSeconds(5);
            _wakeTimer.Tick += delegate
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _lastTick).TotalSeconds > 60)
                {
                    _controller.RestartIfConnected("The computer woke from sleep. Restarting the proxy.");
                }
                _lastTick = now;
            };
            _wakeTimer.Start();

            ApplyState(ProxyState.Disconnected, null);
            AppDomain.CurrentDomain.ProcessExit += delegate { _controller.Dispose(); };
            if (!Platform.IsWindows)
            {
                // A terminated app (SIGTERM from the desktop or a logout, Ctrl+C in a
                // terminal) must take its proxy with it, like a closed window does.
                foreach (PosixSignal signal in new PosixSignal[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
                {
                    try
                    {
                        _signals.Add(PosixSignalRegistration.Create(signal, context =>
                        {
                            _controller.Dispose();
                            context.Cancel = false; // then let the process end
                        }));
                    }
                    catch
                    {
                        // Not supported here; the ProcessExit path remains.
                    }
                }
            }
        }

        private readonly List<PosixSignalRegistration> _signals = new List<PosixSignalRegistration>();

        // ------------------------------------------------------------------
        // Icons and notification-area icon
        // ------------------------------------------------------------------

        private void LoadIcons()
        {
            string[] names = new string[] { "off", "busy", "good", "bad" }; // indexed by ProxyState
            for (int i = 0; i < 4; i++)
            {
                using (Stream s = AssetLoader.Open(new Uri("avares://CloudSqlProxyManager/assets/icon-" + names[i] + ".png")))
                {
                    _icons[i] = new WindowIcon(new Bitmap(s));
                }
            }
        }

        private void BuildTray()
        {
            try
            {
                NativeMenu menu = new NativeMenu();
                NativeMenuItem open = new NativeMenuItem("Open");
                open.Click += delegate { RestoreFromTray(); };
                _trayConnect = new NativeMenuItem("Connect");
                _trayConnect.Click += async delegate { RestoreFromTray(); await DoConnect(); };
                _trayDisconnect = new NativeMenuItem("Disconnect");
                _trayDisconnect.Click += delegate { _controller.Disconnect(); };
                NativeMenuItem exit = new NativeMenuItem("Exit");
                exit.Click += delegate { Close(); };
                menu.Items.Add(open);
                menu.Items.Add(_trayConnect);
                menu.Items.Add(_trayDisconnect);
                menu.Items.Add(new NativeMenuItemSeparator());
                menu.Items.Add(exit);

                _tray = new TrayIcon();
                _tray.Icon = _icons[0];
                _tray.ToolTipText = AppTitle;
                _tray.Menu = menu;
                _tray.Clicked += delegate { RestoreFromTray(); };
                TrayIcons icons = new TrayIcons();
                icons.Add(_tray);
                TrayIcon.SetIcons(Application.Current, icons);
            }
            catch
            {
                // No notification area on this desktop; the window still works.
                _tray = null;
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        // ------------------------------------------------------------------
        // Settings and saved connections
        // ------------------------------------------------------------------

        private void LoadSettingsIntoUi()
        {
            _loading = true;
            AutoBox.IsChecked = _settings.AutoReconnect;
            HideBox.IsChecked = _settings.HideOnConnect;
            if (Platform.IsMac) HideBox.Content = "Minimise this window once connected";
            string proxy = _settings.ProxyPath;
            if (string.IsNullOrWhiteSpace(proxy) || !File.Exists(proxy))
            {
                // Empty means "use the built-in proxy" when this build carries one.
                proxy = BundledProxy.IsAvailable ? "" : ProxyLocator.Detect();
            }
            ProxyBox.Text = proxy;
            _loading = false;

            // Open on the first saved connection whose port is free, so a second
            // copy of the app starts on the next instance rather than a clash.
            ConnectionProfile pick = null;
            foreach (ConnectionProfile p in _settings.Connections)
            {
                if (!PortInspector.IsInUse(p.Port)) { pick = p; break; }
            }
            if (pick == null && _settings.Connections.Count > 0) pick = _settings.Connections[0];

            RefreshSavedList(pick != null ? pick.Key() : null);
            if (pick != null)
            {
                FillFields(pick);
            }
            else
            {
                // First run: start on the known project; its region is filled in and its
                // instances offered. (Field events are wired after this, so sync by hand.)
                List<string[]> known = KnownInstances.All();
                if (known.Count > 0) ProjectBox.Text = known[0][0]; else RegionBox.Text = Regions[0];
                SyncLists(true, true);
                UpdateKeyHint();
                UpdateIdleDetail();
            }
            UpdateProxyHint();
            UpdatePortHint();
            SetAdvancedOpen((ProxyBox.Text ?? "").Trim().Length > 0 || (ExtraBox.Text ?? "").Trim().Length > 0);
        }

        private void RefreshSavedList(string selectKey)
        {
            _loading = true;
            string project = ProjectBox.Text;

            List<ConnectionProfile> items = new List<ConnectionProfile>(_settings.Connections);
            SavedBox.ItemsSource = items;
            int select = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (selectKey != null && items[i].Key() == selectKey) select = i;
            }
            SavedBox.SelectedIndex = select;
            SavedBox.IsEnabled = items.Count > 0 && _state == ProxyState.Disconnected;
            RemoveButton.IsEnabled = select >= 0 && _state == ProxyState.Disconnected;

            // Known projects first, in their listed order, then any others used before.
            List<string> projects = new List<string>();
            foreach (string[] entry in Catalogue()) AddDistinct(projects, entry[0]);
            SetItems(ProjectBox, projects, project);
            SyncLists(false, true);
            _loading = false;
        }

        private void FillFields(ConnectionProfile p)
        {
            _loading = true;
            ProjectBox.Text = p.Project;
            RegionBox.Text = p.Region;
            InstanceBox.Text = p.Instance;
            PortBox.Value = Math.Max(1, Math.Min(65535, p.Port));
            KeyBox.Text = p.CredentialsFile ?? "";
            ExtraBox.Text = p.ExtraArgs ?? "";
            SyncLists(false, true);
            _loading = false;
            if ((ExtraBox.Text ?? "").Trim().Length > 0) SetAdvancedOpen(true);
            UpdateKeyHint();
            UpdatePortHint();
            UpdateIdleDetail();
        }

        private void RemoveSelectedConnection()
        {
            ConnectionProfile p = SavedBox.SelectedItem as ConnectionProfile;
            if (p == null) return;
            _settings = SettingsStore.RemoveConnection(p.Key());
            RefreshSavedList(null);
        }

        // ------------------------------------------------------------------
        // Project > Region > Instance stay in step with each other
        // ------------------------------------------------------------------

        /// <summary>Every instance the app knows of, as { project, region, instance }: the built-in list plus saved connections.</summary>
        private List<string[]> Catalogue()
        {
            List<string[]> all = KnownInstances.All();
            if (_settings != null)
            {
                foreach (ConnectionProfile p in _settings.Connections)
                {
                    all.Add(new string[] { p.Project, p.Region, p.Instance });
                }
            }
            return all;
        }

        /// <summary>
        /// Rebuilds the Region list from the chosen project and the Instance list
        /// from the chosen project and region. With enforce, a region or instance
        /// that does not belong to the new choice is replaced or cleared. A
        /// project the app has never seen is left free: any region, any instance.
        /// </summary>
        private void SyncLists(bool enforce, bool rebuildRegions)
        {
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                List<string[]> all = Catalogue();
                string project = Text(ProjectBox), region = Text(RegionBox), instance = Text(InstanceBox);

                List<string> regions = new List<string>();
                foreach (string[] entry in all)
                {
                    if (Same(entry[0], project)) AddDistinct(regions, entry[1]);
                }
                bool knownProject = regions.Count > 0;
                if (!knownProject)
                {
                    foreach (string r in Regions) AddDistinct(regions, r);
                }
                else if (enforce && rebuildRegions && !ContainsText(regions, region))
                {
                    region = regions[0];
                }
                if (rebuildRegions) SetItems(RegionBox, regions, region);

                List<string> instances = new List<string>();
                foreach (string[] entry in all)
                {
                    if (Same(entry[0], project) && Same(entry[1], region)) AddDistinct(instances, entry[2]);
                }
                if (enforce && knownProject && instance.Length > 0 && !ContainsText(instances, instance))
                {
                    instance = "";
                }
                SetItems(InstanceBox, instances, instance);
            }
            finally
            {
                _loading = wasLoading;
            }
        }

        private static string Text(ComboField box)
        {
            return box.Text.Trim();
        }

        private static void SetItems(ComboField box, List<string> items, string text)
        {
            box.SetItems(items);
            box.Text = text ?? "";
        }

        private static void AddDistinct(List<string> list, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (string existing in list)
            {
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(value);
        }

        private static bool Same(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsText(List<string> list, string value)
        {
            foreach (string item in list)
            {
                if (Same(item, value)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------

        private void WireEvents()
        {
            ActionButton.Click += async delegate
            {
                if (_state == ProxyState.Disconnected) await DoConnect(); else _controller.Disconnect();
            };
            SavedBox.SelectionChanged += delegate
            {
                if (_loading) return;
                ConnectionProfile p = SavedBox.SelectedItem as ConnectionProfile;
                RemoveButton.IsEnabled = p != null;
                if (p != null) FillFields(p);
            };
            RemoveButton.Click += delegate { RemoveSelectedConnection(); };

            ProjectBox.TextChanged += delegate { OnProjectOrRegionEdited(true); };
            RegionBox.TextChanged += delegate { OnProjectOrRegionEdited(false); };
            InstanceBox.TextChanged += delegate { OnInstanceTextChanged(); };

            PortBox.ValueChanged += delegate { UpdatePortHint(); UpdateIdleDetail(); };
            PortSuggest.Click += delegate { if (_suggestedPort > 0) PortBox.Value = _suggestedPort; };

            KeyBox.TextChanged += delegate { UpdateKeyHint(); };
            KeyBrowse.Click += async delegate { await BrowseKey(); };
            AdvancedToggle.Click += delegate { SetAdvancedOpen(!_advancedOpen); };
            ProxyBox.TextChanged += delegate { UpdateProxyHint(); };
            ProxyBrowse.Click += async delegate { await BrowseProxy(); };
            ProxyDownload.Click += delegate { OpenUrl(DownloadUrl); };

            Closing += OnClosing;
            Closed += delegate
            {
                _closing = true;
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                _portTimer.Stop();
                _wakeTimer.Stop();
                _controller.Dispose();
                if (_tray != null) { _tray.IsVisible = false; _tray.Dispose(); }
            };
            PropertyChanged += (s, e) =>
            {
                // Minimising hides the window in the notification area, when there is one.
                if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized && _tray != null && !Platform.IsMac)
                {
                    Hide();
                }
            };
        }

        private void OnProjectOrRegionEdited(bool projectChanged)
        {
            if (_loading) return;
            SyncLists(true, projectChanged);
            UpdateIdleDetail();
        }

        private void OnInstanceTextChanged()
        {
            if (_loading) return;
            // Pasting a full "project:region:instance" name fills all three fields.
            string[] parts = Text(InstanceBox).Split(':');
            if (parts.Length == 3 && parts[0].Length > 0 && parts[1].Length > 0 && parts[2].Length > 0)
            {
                _loading = true;
                ProjectBox.Text = parts[0];
                RegionBox.Text = parts[1];
                InstanceBox.Text = parts[2];
                SyncLists(false, true);
                _loading = false;
            }
            UpdateIdleDetail();
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            _controller.CheckNetworkNow();
        }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_confirmedClose || _state == ProxyState.Disconnected) return;
            e.Cancel = true;
            bool yes = await Dialogs.Ask(this, "The tunnel to " + _activeInstance + " is active. Disconnect and exit?", "Disconnect and exit", "Keep running");
            if (yes)
            {
                _confirmedClose = true;
                Close();
            }
        }

        // ------------------------------------------------------------------
        // Field hints
        // ------------------------------------------------------------------

        /// <summary>While idle, the status band says what Connect is about to do.</summary>
        private void UpdateIdleDetail()
        {
            if (_loading || _state != ProxyState.Disconnected) return;
            SetDetail(IdleDetail());
        }

        private string IdleDetail()
        {
            string project = Text(ProjectBox), region = Text(RegionBox), instance = Text(InstanceBox);
            if (project.Length == 0 || region.Length == 0 || instance.Length == 0)
            {
                return "Choose a project, region and instance, then connect.";
            }
            return project + ":" + region + ":" + instance + "  →  127.0.0.1:" + CurrentPort();
        }

        private void SetDetail(string detail)
        {
            DetailText.Text = detail ?? "";
            ToolTip.SetTip(DetailText, detail ?? "");
        }

        private int CurrentPort()
        {
            decimal? value = PortBox.Value;
            int port = value.HasValue ? (int)value.Value : 3306;
            return Math.Max(1, Math.Min(65535, port));
        }

        private void UpdatePortHint()
        {
            if (_loading || _closing) return;
            int port = CurrentPort();
            int serial = ++_portHintSerial;
            _suggestedPort = 0;

            if (_state != ProxyState.Disconnected && port == _activePort)
            {
                SetPortHint("Port " + port + " is used by this tunnel.", "MutedBrush", 0);
                return;
            }

            if (!PortInspector.IsInUse(port))
            {
                SetPortHint("Port " + port + " is free.", "GoodBrush", 0);
                return;
            }

            // Naming the owner runs a system tool on Linux and macOS: do it off the UI thread.
            Task.Run(() =>
            {
                string owner = PortInspector.DescribeOwner(port);
                int next = PortInspector.NextFree(port + 1);
                Dispatcher.UIThread.Post(() =>
                {
                    if (serial != _portHintSerial || _closing) return;
                    string text = "Port " + port + " is in use" + (owner != null ? " by " + owner : "") + ".";
                    _suggestedPort = next;
                    SetPortHint(text, "BadBrush", next);
                });
            });
        }

        private void SetPortHint(string text, string brushKey, int suggest)
        {
            PortHint.Text = text;
            PortHint.Foreground = Brush(brushKey);
            PortSuggest.Content = "Use " + suggest;
            PortSuggest.IsVisible = suggest > 0;
        }

        private void UpdateKeyHint()
        {
            if (_loading) return;
            string path = CleanPath(KeyBox.Text);
            if (path.Length == 0)
            {
                SetHint(KeyHint, "Leave empty to use your own gcloud login (application-default credentials).", "MutedBrush");
                return;
            }
            if (!File.Exists(path))
            {
                SetHint(KeyHint, "File not found.", "BadBrush");
                return;
            }
            CredentialsInfo info = CredentialsInfo.Read(path);
            SetHint(KeyHint, info.Summary(), info.Problem != null ? "BadBrush" : "MutedBrush");
            if (info.ProjectId != null && Text(ProjectBox).Length == 0) ProjectBox.Text = info.ProjectId;
        }

        private void UpdateProxyHint()
        {
            string path = CleanPath(ProxyBox.Text);
            ProxyDownload.IsVisible = false;
            if (path.Length == 0)
            {
                if (BundledProxy.IsAvailable)
                {
                    SetHint(ProxyHint, "Empty = the built-in cloud-sql-proxy " + BundledProxy.Version + ". Choose a file only to use a different one.", "MutedBrush");
                }
                else
                {
                    SetHint(ProxyHint, "This build has no built-in proxy.", "MutedBrush");
                    ProxyDownload.IsVisible = true;
                }
            }
            else if (!File.Exists(path))
            {
                SetHint(ProxyHint, "File not found.", "BadBrush");
            }
            else
            {
                SetHint(ProxyHint, BundledProxy.IsAvailable ? "This file is used instead of the built-in proxy." : "", "MutedBrush");
            }
        }

        private void SetHint(TextBlock block, string text, string brushKey)
        {
            block.Text = text;
            block.Foreground = Brush(brushKey);
        }

        private IBrush Brush(string key)
        {
            object value;
            if (Application.Current != null && Application.Current.TryFindResource(key, out value) && value is IBrush) return (IBrush)value;
            return Brushes.Gray;
        }

        private void SetAdvancedOpen(bool open)
        {
            _advancedOpen = open;
            AdvancedToggle.Content = open ? "Hide advanced options" : "Advanced options";
            ProxyLabel.IsVisible = open;
            ProxyBox.IsVisible = open;
            ProxyBrowse.IsVisible = open;
            ProxyHintRow.IsVisible = open;
            ExtraLabel.IsVisible = open;
            ExtraBox.IsVisible = open;
        }

        private static string CleanPath(string text)
        {
            return (text ?? "").Trim().Trim('"').Trim();
        }

        private async Task BrowseKey()
        {
            string picked = await PickFile("Choose the service account key file", "JSON key files", new string[] { "*.json" }, KeyBox.Text);
            if (picked != null) KeyBox.Text = picked;
        }

        private async Task BrowseProxy()
        {
            string picked = await PickFile("Choose the cloud-sql-proxy program", "Programs", Platform.IsWindows ? new string[] { "*.exe" } : new string[] { "*" }, ProxyBox.Text);
            if (picked != null) ProxyBox.Text = picked;
        }

        private async Task<string> PickFile(string title, string typeName, string[] patterns, string currentPath)
        {
            FilePickerOpenOptions options = new FilePickerOpenOptions();
            options.Title = title;
            options.AllowMultiple = false;
            FilePickerFileType type = new FilePickerFileType(typeName);
            type.Patterns = patterns;
            options.FileTypeFilter = new FilePickerFileType[] { type, FilePickerFileTypes.All };
            try
            {
                string dir = Path.GetDirectoryName(CleanPath(currentPath));
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir);
                }
            }
            catch
            {
            }
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(options);
            if (files.Count == 0) return null;
            return files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Connect / disconnect
        // ------------------------------------------------------------------

        private async Task DoConnect()
        {
            if (_state != ProxyState.Disconnected) return;

            string project = Text(ProjectBox), region = Text(RegionBox), instance = Text(InstanceBox);
            if (project.Length == 0 || region.Length == 0 || instance.Length == 0)
            {
                await Dialogs.Warn(this, "Fill in the project, region and instance first.");
                return;
            }
            if (HasBadChars(project) || HasBadChars(region) || HasBadChars(instance))
            {
                await Dialogs.Warn(this, "Project, region and instance cannot contain spaces, quotes or colons.");
                return;
            }

            string key = CleanPath(KeyBox.Text);
            if (key.Length > 0 && !File.Exists(key))
            {
                await Dialogs.Warn(this, "The key file was not found:\n" + key);
                return;
            }

            int port = CurrentPort();
            if (PortInspector.IsInUse(port) || !ProxyController.IsPortFree(port))
            {
                string owner = await Task.Run(() => PortInspector.DescribeOwner(port));
                int next = PortInspector.NextFree(port + 1);
                await Dialogs.Warn(this, "Local port " + port + " is already in use" + (owner != null ? " by " + owner : "") + ".\n\n"
                    + "Choose a different local port" + (next > 0 ? ", for example " + next : "") + ".");
                UpdatePortHint();
                return;
            }

            string typedProxy = CleanPath(ProxyBox.Text);
            string proxy = await ResolveProxy(typedProxy);
            if (proxy == null) return;

            ConnectionProfile profile = new ConnectionProfile();
            profile.Project = project;
            profile.Region = region;
            profile.Instance = instance;
            profile.Port = port;
            profile.CredentialsFile = key;
            profile.ExtraArgs = (ExtraBox.Text ?? "").Trim();

            _settings = SettingsStore.SaveConnection(profile, typedProxy, AutoBox.IsChecked == true, HideBox.IsChecked == true);
            RefreshSavedList(profile.Key());

            _activeInstance = instance;
            _activePort = port;

            ProxyOptions options = new ProxyOptions();
            options.ProxyPath = proxy;
            options.ConnectionName = profile.ConnectionName();
            options.Port = port;
            options.CredentialsFile = key;
            options.ExtraArgs = profile.ExtraArgs;

            _controller.AutoReconnect = AutoBox.IsChecked == true;
            _controller.Connect(options);
        }

        /// <summary>A chosen file wins; otherwise the built-in proxy, unpacked on first use.</summary>
        private async Task<string> ResolveProxy(string typed)
        {
            if (typed.Length > 0)
            {
                if (File.Exists(typed)) return typed;
                SetAdvancedOpen(true);
                await Dialogs.Warn(this, "The proxy program was not found:\n" + typed + "\n\nClear the field to use the built-in proxy.");
                return null;
            }

            string overridePath = ProxyLocator.FromEnvironment();
            if (overridePath != null) return overridePath;

            if (BundledProxy.IsAvailable)
            {
                try
                {
                    ActionButton.IsEnabled = false;
                    SetDetail("Unpacking the built-in proxy…");
                    return await Task.Run(() => BundledProxy.EnsureExtracted());
                }
                catch (Exception ex)
                {
                    await Dialogs.Warn(this, ex.Message);
                    return null;
                }
                finally
                {
                    ActionButton.IsEnabled = true;
                }
            }

            SetAdvancedOpen(true);
            await Dialogs.Warn(this, "This build has no built-in proxy.\n\nDownload cloud-sql-proxy from Google and choose "
                + "the program under Advanced options > Proxy program.");
            return null;
        }

        private static bool HasBadChars(string value)
        {
            return value.IndexOfAny(new char[] { ' ', '\t', '"', ':' }) >= 0;
        }

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        private void ApplyState(ProxyState state, string detail)
        {
            if (_closing) return;
            ProxyState previous = _state;
            _state = state;
            bool idle = state == ProxyState.Disconnected;

            string text, light, tint;
            switch (state)
            {
                case ProxyState.Connecting:
                    text = "Connecting…"; light = "BusyBrush"; tint = "BusyTintBrush";
                    break;
                case ProxyState.Connected:
                    text = "Connected"; light = "GoodBrush"; tint = "GoodTintBrush";
                    if (string.IsNullOrEmpty(detail)) detail = _activeInstance + " is available at 127.0.0.1:" + _activePort;
                    break;
                case ProxyState.Reconnecting:
                    text = "Reconnecting…"; light = "BadBrush"; tint = "BadTintBrush";
                    break;
                default:
                    text = "Disconnected"; light = "OffBrush"; tint = "OffTintBrush";
                    break;
            }
            if (idle && string.IsNullOrEmpty(detail)) detail = IdleDetail();

            Header.Background = Brush(tint);
            Dot.Fill = Brush(light);
            StateText.Text = text;
            SetDetail(detail);

            ActionButton.Content = idle ? "Connect" : "Disconnect";
            if (idle) ActionButton.Classes.Add("primary"); else ActionButton.Classes.Remove("primary");
            ActionButton.IsDefault = idle; // Enter must never disconnect by accident

            SavedBox.IsEnabled = idle && SavedBox.ItemCount > 0;
            RemoveButton.IsEnabled = idle && SavedBox.SelectedIndex >= 0;
            ProjectBox.IsEnabled = idle;
            RegionBox.IsEnabled = idle;
            InstanceBox.IsEnabled = idle;
            PortBox.IsEnabled = idle;
            KeyBox.IsEnabled = idle;
            KeyBrowse.IsEnabled = idle;
            ProxyBox.IsEnabled = idle;
            ProxyBrowse.IsEnabled = idle;
            ExtraBox.IsEnabled = idle;
            AutoBox.IsEnabled = idle;
            HideBox.IsEnabled = idle;
            if (_trayConnect != null) _trayConnect.IsEnabled = idle;
            if (_trayDisconnect != null) _trayDisconnect.IsEnabled = !idle;

            Title = idle || _activeInstance == null
                ? AppTitle
                : _activeInstance + " → :" + _activePort + "  ·  " + text + "  —  " + AppTitle;
            Icon = _icons[(int)state];
            if (_tray != null)
            {
                _tray.Icon = _icons[(int)state];
                _tray.ToolTipText = idle || _activeInstance == null
                    ? AppTitle + " (disconnected)"
                    : _activeInstance + " :" + _activePort + " - " + text;
            }

            UpdatePortHint();

            // The tunnel is up: get out of the way. Only on the user's own Connect,
            // never on a reconnect, so an open window stays open.
            if (previous == ProxyState.Connecting && state == ProxyState.Connected && HideBox.IsChecked == true)
            {
                if (_tray != null && !Platform.IsMac) Hide(); else WindowState = WindowState.Minimized;
            }
        }

        private void AppendLog(string line)
        {
            if (_closing) return;
            string current = LogBox.Text ?? "";
            if (current.Length > 200000) current = current.Substring(current.Length - 100000);
            LogBox.Text = current + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text.Length;
        }
    }
}
