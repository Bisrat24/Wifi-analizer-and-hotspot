using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WifiAnalyzerHotspot.Models;
using WifiAnalyzerHotspot.Services;

namespace WifiAnalyzerHotspot.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly WifiScannerService _scannerService = new();
        private readonly ChannelAnalysisService _analysisService = new();
        private readonly SpeedTestService _speedTestService = new();
        private readonly NetworkMonitorService _networkMonitorService = new();
        private readonly HotspotService _hotspotService = new();
        private readonly ConnectionStabilizerService _stabilizerService = new();

        private readonly DispatcherTimer _scanTimer;
        private readonly DispatcherTimer _trafficTimer;
        private CancellationTokenSource? _speedTestCts;
        private int _hotspotRefreshCounter = 0;

        private string _currentTab = "Analyzer";
        private string _selectedBand = "2.4 GHz";
        private string _searchFilter = string.Empty;
        private bool _isScanning;
        private bool _autoScanEnabled = true;
        private string _scanStatus = "Ready";

        private ConnectedInterfaceInfo _connectedInterface = new();
        private SpeedTestMetrics _speedMetrics = new();
        private HotspotModel _hotspot = new();
        private LiveTrafficPoint _currentTraffic = new();
        private ConnectionStabilizerModel _stabilizer = new();

        private string _hotspotSsidInput = "Wifi_Hotspot";
        private string _hotspotPasswordInput = "HotspotPass123";
        private string _hotspotBandInput = "2.4 GHz";
        private string _hotspotChannelChoice = "Auto (Recommended)";
        private string _bestChannelAdvice = "Channel 1 is recommended for Hotspot.";

        public ObservableCollection<WifiNetwork> Networks { get; } = new();
        public ObservableCollection<WifiNetwork> FilteredNetworks { get; } = new();
        public ObservableCollection<ChannelRating> Ratings24G { get; } = new();
        public ObservableCollection<ChannelRating> Ratings5G { get; } = new();
        public ObservableCollection<string> AvailableHotspotChannels { get; } = new();

        public string CurrentTab
        {
            get => _currentTab;
            set { _currentTab = value; OnPropertyChanged(); }
        }

        public string SelectedBand
        {
            get => _selectedBand;
            set
            {
                _selectedBand = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        public string SearchFilter
        {
            get => _searchFilter;
            set
            {
                _searchFilter = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        public bool IsScanning
        {
            get => _isScanning;
            set { _isScanning = value; OnPropertyChanged(); }
        }

        public bool AutoScanEnabled
        {
            get => _autoScanEnabled;
            set
            {
                _autoScanEnabled = value;
                OnPropertyChanged();
                if (_autoScanEnabled) _scanTimer.Start();
                else _scanTimer.Stop();
            }
        }

        public string ScanStatus
        {
            get => _scanStatus;
            set { _scanStatus = value; OnPropertyChanged(); }
        }

        public ConnectedInterfaceInfo ConnectedInterface
        {
            get => _connectedInterface;
            set { _connectedInterface = value; OnPropertyChanged(); }
        }

        public SpeedTestMetrics SpeedMetrics
        {
            get => _speedMetrics;
            set
            {
                _speedMetrics = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanStartSpeedTest));
            }
        }

        public bool CanStartSpeedTest => !SpeedMetrics.IsTesting;

        public HotspotModel Hotspot
        {
            get => _hotspot;
            set { _hotspot = value; OnPropertyChanged(); }
        }

        public LiveTrafficPoint CurrentTraffic
        {
            get => _currentTraffic;
            set { _currentTraffic = value; OnPropertyChanged(); }
        }

        public string HotspotSsidInput
        {
            get => _hotspotSsidInput;
            set { _hotspotSsidInput = value; OnPropertyChanged(); }
        }

        public string HotspotPasswordInput
        {
            get => _hotspotPasswordInput;
            set { _hotspotPasswordInput = value; OnPropertyChanged(); }
        }

        public string HotspotBandInput
        {
            get => _hotspotBandInput;
            set { _hotspotBandInput = value; OnPropertyChanged(); }
        }

        public string HotspotChannelChoice
        {
            get => _hotspotChannelChoice;
            set
            {
                _hotspotChannelChoice = value;
                OnPropertyChanged();
                if (_hotspotChannelChoice.Contains("5 GHz") || _hotspotChannelChoice.Contains("5GHz"))
                {
                    HotspotBandInput = "5 GHz";
                }
                else if (_hotspotChannelChoice.Contains("Auto"))
                {
                    HotspotBandInput = "Auto / Any";
                }
                else
                {
                    HotspotBandInput = "2.4 GHz";
                }
            }
        }

        public string BestChannelAdvice
        {
            get => _bestChannelAdvice;
            set { _bestChannelAdvice = value; OnPropertyChanged(); }
        }

        public ConnectionStabilizerModel Stabilizer
        {
            get => _stabilizer;
            set { _stabilizer = value; OnPropertyChanged(); }
        }

        public ICommand ScanNowCommand { get; }
        public ICommand StartSpeedTestCommand { get; }
        public ICommand CancelSpeedTestCommand { get; }
        public ICommand ToggleHotspotCommand { get; }
        public ICommand SaveHotspotConfigCommand { get; }
        public ICommand RefreshHotspotCommand { get; }
        public ICommand SetTabCommand { get; }
        public ICommand SetBandCommand { get; }
        public ICommand ToggleKeepAliveCommand { get; }
        public ICommand ToggleReconnectCommand { get; }
        public ICommand ClearStabilizerLogsCommand { get; }
        public ICommand DisconnectClientCommand { get; }
        public ICommand BlockClientCommand { get; }
        public ICommand UnblockClientCommand { get; }

        public MainViewModel()
        {
            ScanNowCommand = new RelayCommand(async () => await PerformScanAsync());
            StartSpeedTestCommand = new RelayCommand(
                async () => await StartSpeedTestAsync(),
                () => CanStartSpeedTest);
            CancelSpeedTestCommand = new RelayCommand(CancelSpeedTest);
            ToggleHotspotCommand = new RelayCommand(async () => await ToggleHotspotAsync());
            SaveHotspotConfigCommand = new RelayCommand(async () => await SaveHotspotConfigAsync());
            RefreshHotspotCommand = new RelayCommand(async () => await RefreshHotspotAsync());
            SetTabCommand = new RelayCommand(param => CurrentTab = param?.ToString() ?? "Analyzer");
            SetBandCommand = new RelayCommand(param => SelectedBand = param?.ToString() ?? "2.4 GHz");

            DisconnectClientCommand = new RelayCommand<HotspotClientInfo>(async client =>
            {
                if (client == null) return;
                var res = await _hotspotService.DisconnectClientAsync(client.MacAddress, client.IpAddress);
                Hotspot.StatusMessage = res.message;
                Hotspot.Clients.Remove(client);
                Hotspot.ClientCount = Hotspot.Clients.Count;
            });

            BlockClientCommand = new RelayCommand<HotspotClientInfo>(async client =>
            {
                if (client == null) return;
                var res = await _hotspotService.BlockClientAsync(client.MacAddress, client.IpAddress);
                if (!Hotspot.BlockedClients.Any(b => b.MacAddress.Equals(client.MacAddress, StringComparison.OrdinalIgnoreCase)))
                {
                    Hotspot.BlockedClients.Add(new BlockedClientInfo
                    {
                        HostName = client.HostName,
                        MacAddress = client.MacAddress,
                        IpAddress = client.IpAddress,
                        BlockedTime = DateTime.Now.ToString("HH:mm:ss"),
                        Reason = "Blocked by Administrator"
                    });
                }
                Hotspot.Clients.Remove(client);
                Hotspot.ClientCount = Hotspot.Clients.Count;
                Hotspot.StatusMessage = res.message;
            });

            UnblockClientCommand = new RelayCommand<BlockedClientInfo>(async blocked =>
            {
                if (blocked == null) return;
                var res = await _hotspotService.UnblockClientAsync(blocked.MacAddress, blocked.IpAddress);
                Hotspot.BlockedClients.Remove(blocked);
                Hotspot.StatusMessage = res.message;
                await RefreshHotspotAsync();
            });

            ToggleKeepAliveCommand = new RelayCommand(() =>
            {
                if (Stabilizer.IsKeepAliveActive)
                {
                    _stabilizerService.StopKeepAlive();
                    Stabilizer.IsKeepAliveActive = false;
                    Stabilizer.KeepAliveStatusText = "Keep-Alive is Stopped";
                }
                else
                {
                    _stabilizerService.StartKeepAlive(Stabilizer);
                }
            });

            ToggleReconnectCommand = new RelayCommand(() =>
            {
                if (Stabilizer.IsReconnectActive)
                {
                    _stabilizerService.StopActiveReconnect();
                    Stabilizer.IsReconnectActive = false;
                    Stabilizer.ReconnectStatusText = "Active Reconnect is Idle";
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(Stabilizer.TargetSsid) || Stabilizer.TargetSsid == "Africa")
                    {
                        if (!string.IsNullOrWhiteSpace(ConnectedInterface.Ssid) && !ConnectedInterface.Ssid.StartsWith("Disconnected"))
                        {
                            Stabilizer.TargetSsid = ConnectedInterface.Ssid;
                        }
                        else
                        {
                            var ifaces = NetworkInterface.GetAllNetworkInterfaces();
                            bool isEth = ifaces.Any(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet && i.OperationalStatus == OperationalStatus.Up);
                            Stabilizer.TargetSsid = isEth ? "Ethernet (Cable Connection)" : (ConnectedInterface.Ssid ?? "Ethernet");
                        }
                    }
                    _stabilizerService.StartActiveReconnect(Stabilizer);
                }
            });

            ClearStabilizerLogsCommand = new RelayCommand(() =>
            {
                Stabilizer.ReconnectLogs.Clear();
            });

            // Instant OS network change notification (fires in <50ms when Wi-Fi is toggled off or on)
            NetworkChange.NetworkAddressChanged += (s, e) => CheckFastWifiState();
            NetworkChange.NetworkAvailabilityChanged += (s, e) => CheckFastWifiState();

            // Wi-Fi scanner timer: runs every 5 seconds on UI Dispatcher
            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _scanTimer.Tick += async (s, e) => await PerformScanAsync();

            // Real-time adapter traffic monitor & client refresh timer: runs every 1 second
            _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _trafficTimer.Tick += async (s, e) =>
            {
                try
                {
                    CurrentTraffic = _networkMonitorService.GetCurrentTraffic();
                    CheckFastWifiState();

                    // Every 3 seconds, if hotspot is running or tab is Hotspot, auto-refresh clients
                    _hotspotRefreshCounter++;
                    if (_hotspotRefreshCounter >= 3)
                    {
                        _hotspotRefreshCounter = 0;
                        if (Hotspot.IsRunning && !Hotspot.IsBusy)
                        {
                            await RefreshHotspotAsync();
                        }
                    }
                }
                catch { }
            };
            _trafficTimer.Start();

            // Initialize available hotspot channels (default to Ethernet / cable mode until scan completes)
            UpdateAvailableHotspotChannels(0);

            // Queue initial scans safely on UI dispatcher after window load
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                await PerformScanAsync();
                await RefreshHotspotAsync();
                _scanTimer.Start();
            }, DispatcherPriority.Background);
        }

        public void UpdateAvailableHotspotChannels(int activeChannel)
        {
            var previousChoice = HotspotChannelChoice;
            AvailableHotspotChannels.Clear();

            if (activeChannel > 0)
            {
                // Connected to Wi-Fi: Single-Channel Concurrency applies
                AvailableHotspotChannels.Add($"Auto / Synchronized with active Wi-Fi (Channel {activeChannel})");
                AvailableHotspotChannels.Add($"Channel {activeChannel} (2.4 GHz) - [ACTIVE / SYNCHRONIZED with {ConnectedInterface.Ssid}]");

                for (int ch = 1; ch <= 11; ch++)
                {
                    if (ch == activeChannel) continue;
                    AvailableHotspotChannels.Add($"Channel {ch} (2.4 GHz) - Currently Unavailable [Locked to active Wi-Fi Ch {activeChannel} • Available on Ethernet/Cable]");
                }

                // 5 GHz channels
                int[] fiveGChannels = new[] { 36, 40, 44, 48, 149, 153, 157, 161 };
                foreach (var ch5 in fiveGChannels)
                {
                    AvailableHotspotChannels.Add($"Channel {ch5} (5 GHz) - [High Speed 5 GHz • Available if Dual-Band supported]");
                }
            }
            else
            {
                // On Ethernet / Cable or disconnected from Wi-Fi
                AvailableHotspotChannels.Add("Auto / Dual-Band (Recommended - Available on Ethernet/Cable)");
                for (int ch = 1; ch <= 11; ch++)
                {
                    AvailableHotspotChannels.Add($"Channel {ch} (2.4 GHz) - [Available on Ethernet/Cable]");
                }
                int[] fiveGChannels = new[] { 36, 40, 44, 48, 149, 153, 157, 161 };
                foreach (var ch5 in fiveGChannels)
                {
                    AvailableHotspotChannels.Add($"Channel {ch5} (5 GHz) - [Available on Ethernet/Cable]");
                }
            }

            if (!string.IsNullOrEmpty(previousChoice) && AvailableHotspotChannels.Contains(previousChoice))
            {
                HotspotChannelChoice = previousChoice;
            }
            else
            {
                HotspotChannelChoice = AvailableHotspotChannels.FirstOrDefault() ?? "Auto";
            }
        }

        public async Task PerformScanAsync()
        {
            if (IsScanning) return;
            IsScanning = true;
            ScanStatus = "Scanning Wi-Fi channels...";

            try
            {
                var connInfo = await Task.Run(() => _scannerService.GetConnectedInterfaceInfo());
                var scanResults = await _scannerService.ScanNetworksAsync();
                var ratings24 = await Task.Run(() => _analysisService.Analyze24GChannels(scanResults));
                var ratings5 = await Task.Run(() => _analysisService.Analyze5GChannels(scanResults));

                // If disconnected from Wi-Fi, check if an Ethernet cable connection is active
                if (string.IsNullOrWhiteSpace(connInfo.Ssid) || connInfo.Ssid.Equals("Disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    var ifaces = NetworkInterface.GetAllNetworkInterfaces();
                    bool isEthernetUp = ifaces.Any(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet && i.OperationalStatus == OperationalStatus.Up);
                    if (isEthernetUp)
                    {
                        connInfo.Ssid = "Disconnected (Ethernet active)";
                        connInfo.Description = "Connected via Ethernet Cable • Wi-Fi Analyzer & Hotspot Ready";
                    }
                }

                ConnectedInterface = connInfo;
                if (!string.IsNullOrWhiteSpace(connInfo.Ssid))
                {
                    if (connInfo.Ssid.StartsWith("Disconnected (Ethernet"))
                    {
                        if (string.IsNullOrWhiteSpace(Stabilizer.TargetSsid) || Stabilizer.TargetSsid == "Africa")
                        {
                            Stabilizer.TargetSsid = "Ethernet (Cable Connection)";
                        }
                    }
                    else if (!connInfo.Ssid.StartsWith("Disconnected"))
                    {
                        if (string.IsNullOrWhiteSpace(Stabilizer.TargetSsid) || Stabilizer.TargetSsid == "Africa" || Stabilizer.TargetSsid == "Ethernet (Cable Connection)")
                        {
                            Stabilizer.TargetSsid = connInfo.Ssid;
                        }
                    }
                }
                UpdateAvailableHotspotChannels(connInfo.Channel);

                Networks.Clear();
                foreach (var net in scanResults)
                {
                    Networks.Add(net);
                }
                ApplyFilter();

                Ratings24G.Clear();
                foreach (var r in ratings24) Ratings24G.Add(r);

                Ratings5G.Clear();
                foreach (var r in ratings5) Ratings5G.Add(r);

                var best24 = ratings24.FirstOrDefault(r => r.IsRecommended);
                var best5 = ratings5.FirstOrDefault(r => r.IsRecommended);

                if (best24 != null && best5 != null)
                {
                    BestChannelAdvice = $"Optimal for 2.4 GHz Hotspot: Channel {best24.Channel} ({best24.StatusText}). Cleanest 5 GHz: Channel {best5.Channel}.";
                }

                ScanStatus = $"Last scan: {DateTime.Now:HH:mm:ss} ({Networks.Count} networks detected)";
            }
            catch (Exception ex)
            {
                ScanStatus = $"Scan error: {ex.Message}";
            }
            finally
            {
                IsScanning = false;
            }
        }

        private void CheckFastWifiState()
        {
            try
            {
                var ifaces = NetworkInterface.GetAllNetworkInterfaces();
                var wifiIfaces = ifaces.Where(i => i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && !i.Name.Contains('*')).ToList();

                bool isWifiRadioUp = wifiIfaces.Any(i => i.OperationalStatus == OperationalStatus.Up);
                bool isEthernetUp = ifaces.Any(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet && i.OperationalStatus == OperationalStatus.Up);

                if (wifiIfaces.Count == 0)
                {
                    // No Wi-Fi adapter detected at all
                    if (ConnectedInterface.Ssid != "No Wi-Fi Adapter")
                    {
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            ConnectedInterface = new ConnectedInterfaceInfo
                            {
                                Ssid = "No Wi-Fi Adapter",
                                InterfaceName = "None",
                                Description = "No wireless 802.11 network interface found",
                                Signal = 0,
                                Rssi = -100,
                                Channel = 0,
                                Band = ""
                            };
                            Networks.Clear();
                            FilteredNetworks.Clear();
                            ScanStatus = "No Wi-Fi adapter detected.";
                            UpdateAvailableHotspotChannels(0);
                        });
                    }
                    return;
                }

                if (!isWifiRadioUp)
                {
                    // Wi-Fi adapter is present, but not associated with an AP (e.g. PC is connected to Ethernet cable)
                    string expectedSsid = isEthernetUp ? "Disconnected (Ethernet active)" : "Disconnected";
                    if (ConnectedInterface.Ssid != expectedSsid && ConnectedInterface.Ssid != "Disconnected / Wi-Fi Off")
                    {
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            ConnectedInterface = new ConnectedInterfaceInfo
                            {
                                Ssid = expectedSsid,
                                InterfaceName = wifiIfaces.FirstOrDefault()?.Name ?? "Wi-Fi",
                                Description = isEthernetUp
                                    ? "Connected via Ethernet Cable • Wi-Fi Analyzer & Hotspot Ready"
                                    : "Wi-Fi adapter ready • Disconnected from access point",
                                Signal = 0,
                                Rssi = -100,
                                Channel = 0,
                                Band = ""
                            };
                            // CRITICAL: NEVER call Networks.Clear() or FilteredNetworks.Clear() here!
                            // Wi-Fi adapter continuously scans surrounding networks even when on Ethernet.
                            ScanStatus = isEthernetUp
                                ? "Ethernet cable active • Wi-Fi Analyzer scanning channels..."
                                : "Wi-Fi disconnected • Scanning channels...";
                            UpdateAvailableHotspotChannels(0);
                        });
                    }
                }
                else
                {
                    // If Wi-Fi was previously disconnected and has now reconnected, trigger scan immediately
                    if ((ConnectedInterface.Ssid.StartsWith("Disconnected") || ConnectedInterface.Ssid == "No Wi-Fi Adapter") && !IsScanning)
                    {
                        Application.Current?.Dispatcher.InvokeAsync(async () =>
                        {
                            await PerformScanAsync();
                        });
                    }
                }
            }
            catch { }
        }

        private void ApplyFilter()
        {
            FilteredNetworks.Clear();
            var query = Networks.AsEnumerable();

            if (SelectedBand == "2.4 GHz")
            {
                query = query.Where(n => n.Band.Contains("2.4") || (n.Channel >= 1 && n.Channel <= 14));
            }
            else if (SelectedBand == "5 GHz")
            {
                query = query.Where(n => n.Band.Contains("5") || n.Channel > 14);
            }

            if (!string.IsNullOrWhiteSpace(SearchFilter))
            {
                query = query.Where(n =>
                    n.DisplayName.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) ||
                    n.Bssid.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var item in query)
            {
                FilteredNetworks.Add(item);
            }
        }

        private async Task StartSpeedTestAsync()
        {
            if (SpeedMetrics.IsTesting) return;

            _speedTestCts = new CancellationTokenSource();
            SpeedMetrics.GaugeValue = 0;
            SpeedMetrics.PingMs = 0;
            SpeedMetrics.JitterMs = 0;
            SpeedMetrics.DownloadMbps = 0;
            SpeedMetrics.UploadMbps = 0;

            OnPropertyChanged(nameof(CanStartSpeedTest));
            CommandManager.InvalidateRequerySuggested();

            try
            {
                await _speedTestService.RunFullTestAsync(
                    SpeedMetrics,
                    val => Application.Current?.Dispatcher.Invoke(() => SpeedMetrics.GaugeValue = val),
                    _speedTestCts.Token);
            }
            finally
            {
                OnPropertyChanged(nameof(CanStartSpeedTest));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void CancelSpeedTest()
        {
            _speedTestCts?.Cancel();
            OnPropertyChanged(nameof(CanStartSpeedTest));
            CommandManager.InvalidateRequerySuggested();
        }

        public async Task RefreshHotspotAsync()
        {
            if (Hotspot.IsBusy) return;
            Hotspot.IsBusy = true;
            try
            {
                var current = await _hotspotService.GetStatusAsync(ConnectedInterface.Channel, ConnectedInterface.Band, ConnectedInterface.InterfaceName, HotspotChannelChoice);

                Hotspot.State = current.State;
                Hotspot.Ssid = current.Ssid;
                Hotspot.Passphrase = current.Passphrase;
                Hotspot.Band = current.Band;
                Hotspot.ActiveChannel = current.ActiveChannel;
                Hotspot.ActiveBand = current.ActiveBand;
                Hotspot.ChannelDescription = current.ChannelDescription;
                Hotspot.ClientCount = current.ClientCount;
                Hotspot.StatusMessage = current.StatusMessage;

                if (string.IsNullOrEmpty(HotspotSsidInput) || HotspotSsidInput == "Wifi_Hotspot")
                {
                    HotspotSsidInput = current.Ssid;
                    HotspotPasswordInput = current.Passphrase;
                    HotspotBandInput = current.Band;
                }

                Hotspot.Clients.Clear();
                foreach (var c in current.Clients)
                {
                    if (!Hotspot.BlockedClients.Any(b => b.MacAddress.Equals(c.MacAddress, StringComparison.OrdinalIgnoreCase)))
                    {
                        Hotspot.Clients.Add(c);
                    }
                }
                Hotspot.ClientCount = Hotspot.Clients.Count;
            }
            catch (Exception ex)
            {
                Hotspot.StatusMessage = $"Hotspot check: {ex.Message}";
            }
            finally
            {
                Hotspot.IsBusy = false;
            }
        }

        private async Task ToggleHotspotAsync()
        {
            Hotspot.IsBusy = true;
            try
            {
                if (Hotspot.IsRunning)
                {
                    Hotspot.StatusMessage = "Stopping Hotspot...";
                    var res = await _hotspotService.StopHotspotAsync();
                    Hotspot.StatusMessage = res.message;
                }
                else
                {
                    Hotspot.StatusMessage = "Configuring & Starting Hotspot...";
                    await _hotspotService.ConfigureHotspotAsync(HotspotSsidInput, HotspotPasswordInput, HotspotChannelChoice);
                    var res = await _hotspotService.StartHotspotAsync();
                    Hotspot.StatusMessage = res.message;
                }
                await RefreshHotspotAsync();
            }
            catch (Exception ex)
            {
                Hotspot.StatusMessage = $"Toggle error: {ex.Message}";
            }
            finally
            {
                Hotspot.IsBusy = false;
            }
        }

        private async Task SaveHotspotConfigAsync()
        {
            Hotspot.IsBusy = true;
            try
            {
                Hotspot.StatusMessage = "Updating configuration...";
                var res = await _hotspotService.ConfigureHotspotAsync(HotspotSsidInput, HotspotPasswordInput, HotspotChannelChoice);
                Hotspot.StatusMessage = res.message;
                await RefreshHotspotAsync();
            }
            catch (Exception ex)
            {
                Hotspot.StatusMessage = $"Save error: {ex.Message}";
            }
            finally
            {
                Hotspot.IsBusy = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
