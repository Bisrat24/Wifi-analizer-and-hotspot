using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
        private readonly NetworkSettingsService _settingsService = new();

        private AppSettingsModel _settings = new();
        private bool _isSettingsOpen = false;

        private readonly DispatcherTimer _scanTimer;
        private readonly DispatcherTimer _trafficTimer;
        private CancellationTokenSource? _speedTestCts;
        private int _hotspotRefreshCounter = 0;
        private bool _wasExplicitlyStarted = false;
        private bool _isResettingInProgress = false;
        private bool _isUserActionInProgress = false;
        private int _inTransitionCountSeconds = 0;
        private int _unknownStateCountSeconds = 0;
        private int _noDataCountSeconds = 0;
        private int _resetCooldownSeconds = 0;

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
        public ObservableCollection<RssiHistoryPoint> RssiHistory { get; } = new();
        public List<string> QosLimitOptions { get; } = new() { "Unlimited", "1 Mbps", "2 Mbps", "5 Mbps", "10 Mbps", "20 Mbps" };

        private string _analyzerViewMode = "Spectrum";
        public string AnalyzerViewMode
        {
            get => _analyzerViewMode;
            set
            {
                if (_analyzerViewMode != value)
                {
                    _analyzerViewMode = value;
                    OnPropertyChanged();
                    if (_settings != null)
                    {
                        _settings.AnalyzerViewMode = value;
                        _settingsService.SaveSettings(_settings);
                    }
                }
            }
        }

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

        public AppSettingsModel Settings
        {
            get => _settings;
            set { _settings = value; OnPropertyChanged(); }
        }

        public bool IsSettingsOpen
        {
            get => _isSettingsOpen;
            set { _isSettingsOpen = value; OnPropertyChanged(); }
        }

        public ICommand ScanNowCommand { get; }
        public ICommand StartSpeedTestCommand { get; }
        public ICommand CancelSpeedTestCommand { get; }
        public ICommand ToggleHotspotCommand { get; }
        public ICommand SaveHotspotConfigCommand { get; }
        public ICommand RefreshHotspotCommand { get; }
        public ICommand CopyHotspotDetailsCommand { get; }
        public ICommand CancelHotspotResetCommand { get; }
        public ICommand ResetHotspotNowCommand { get; }
        public ICommand TestWatchdogCommand { get; }
        public ICommand RepairDhcpCommand { get; }
        public ICommand SetTabCommand { get; }
        public ICommand SetBandCommand { get; }
        public ICommand ToggleKeepAliveCommand { get; }
        public ICommand ToggleReconnectCommand { get; }
        public ICommand ClearStabilizerLogsCommand { get; }
        public ICommand DisconnectClientCommand { get; }
        public ICommand BlockClientCommand { get; }
        public ICommand UnblockClientCommand { get; }
        public ICommand ToggleSettingsCommand { get; }
        public ICommand CloseSettingsCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand ResetSettingsCommand { get; }
        public ICommand TestDohCommand { get; }
        public ICommand ApplyDohToAdapterCommand { get; }
        public ICommand RestoreDhcpDnsCommand { get; }
        public ICommand TestCaptivePortalCommand { get; }
        public ICommand TestProxyCommand { get; }
        public ICommand OpenCaptiveBrowserCommand { get; }
        public ICommand ApplyHotspotPriorityCommand { get; }
        public ICommand SetAnalyzerViewModeCommand { get; }
        public ICommand SetClientQosLimitCommand { get; }

        public MainViewModel()
        {
            _settings = _settingsService.LoadSettings();
            _analyzerViewMode = _settings.AnalyzerViewMode ?? "Spectrum";
            ConnectionStabilizerService.CaptivePortalProbeUrl = _settings.CaptivePortalUrl;

            // Load Hotspot & Stabilizer persisted preferences
            Hotspot.WatchdogIntervalSeconds = _settings.HotspotWatchdogIntervalSeconds > 0 ? _settings.HotspotWatchdogIntervalSeconds : 5;
            Stabilizer.KeepAliveLogLimit = _settings.KeepAliveLogLimit > 0 ? _settings.KeepAliveLogLimit : 100;
            Stabilizer.ReconnectLogLimit = _settings.ReconnectLogLimit > 0 ? _settings.ReconnectLogLimit : 100;

            if (_settings.BlockedClients != null)
            {
                foreach (var b in _settings.BlockedClients)
                {
                    Hotspot.BlockedClients.Add(b);
                    HotspotService.RegisterBlockedMac(b.MacAddress);
                }
            }

            Hotspot.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Hotspot.WatchdogIntervalSeconds))
                {
                    _settings.HotspotWatchdogIntervalSeconds = Hotspot.WatchdogIntervalSeconds;
                    _settingsService.SaveSettings(_settings);
                }
            };

            Stabilizer.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Stabilizer.KeepAliveLogLimit))
                {
                    _settings.KeepAliveLogLimit = Stabilizer.KeepAliveLogLimit;
                    _settingsService.SaveSettings(_settings);
                }
                else if (e.PropertyName == nameof(Stabilizer.ReconnectLogLimit))
                {
                    _settings.ReconnectLogLimit = Stabilizer.ReconnectLogLimit;
                    _settingsService.SaveSettings(_settings);
                }
            };

            ApplyHotspotPriorityCommand = new RelayCommand(async () =>
            {
                Hotspot.StatusMessage = "⚡ Engaging Highest PC Priority Mode (RealTime)...";
                var res = await _hotspotService.OptimizePriorityAsync();
                Hotspot.PriorityStatusText = "⚡ Highest PC Priority: REALTIME";
                Hotspot.StatusMessage = res.message;
            });

            ScanNowCommand = new RelayCommand(async () => await PerformScanAsync());
            StartSpeedTestCommand = new RelayCommand(
                async () => await StartSpeedTestAsync(),
                () => CanStartSpeedTest);
            CancelSpeedTestCommand = new RelayCommand(CancelSpeedTest);
            ToggleHotspotCommand = new RelayCommand(async () => await ToggleHotspotAsync());
            SaveHotspotConfigCommand = new RelayCommand(async () => await SaveHotspotConfigAsync());
            RefreshHotspotCommand = new RelayCommand(async () => await RefreshHotspotAsync());
            CopyHotspotDetailsCommand = new RelayCommand(() =>
            {
                try
                {
                    string info = $"SSID: {Hotspot.Ssid}\nPassword: {Hotspot.Passphrase}\nBand: {Hotspot.Band}";
                    Clipboard.SetText(info);
                    Hotspot.StatusMessage = "✓ Hotspot credentials copied to clipboard!";
                }
                catch (Exception ex)
                {
                    Hotspot.StatusMessage = $"Copy failed: {ex.Message}";
                }
            });

            CancelHotspotResetCommand = new RelayCommand(() =>
            {
                Hotspot.IsResetCountdownActive = false;
                _inTransitionCountSeconds = 0;
                _unknownStateCountSeconds = 0;
                _noDataCountSeconds = 0;
                _resetCooldownSeconds = 25;
                Hotspot.StatusMessage = "Auto-reset canceled by user. Monitoring paused for 25s.";
            });

            ResetHotspotNowCommand = new RelayCommand(async () =>
            {
                Hotspot.IsResetCountdownActive = false;
                _inTransitionCountSeconds = 0;
                _unknownStateCountSeconds = 0;
                _noDataCountSeconds = 0;
                await PerformHotspotResetAsync();
            });

            TestWatchdogCommand = new RelayCommand(() =>
            {
                if (_isResettingInProgress) return;
                _resetCooldownSeconds = 0;
                _inTransitionCountSeconds = 0;
                _unknownStateCountSeconds = 0;
                _noDataCountSeconds = 0;
                Hotspot.IsResetCountdownActive = true;
                Hotspot.ResetCountdownSeconds = 30;
                Hotspot.ResetCountdownMessage = "⚠️ Health Check Test: Devices connected are not getting data. Auto-resetting in 30s (click Cancel to dismiss)...";
            });

            RepairDhcpCommand = new RelayCommand(async () =>
            {
                Hotspot.StatusMessage = "Optimizing DHCP scope (192.168.137.1) & restarting SharedAccess...";
                var res = await _hotspotService.OptimizeHotspotDhcpAsync();
                Hotspot.StatusMessage = res.message;
                await RefreshHotspotAsync();
            });

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
                string cleanMac = HotspotModel.NormalizeMac(client.MacAddress);
                var res = await _hotspotService.BlockClientAsync(client.MacAddress, client.IpAddress);
                if (!Hotspot.BlockedClients.Any(b => HotspotModel.NormalizeMac(b.MacAddress) == cleanMac))
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
                var toRemove = Hotspot.Clients.Where(c => HotspotModel.NormalizeMac(c.MacAddress) == cleanMac).ToList();
                foreach (var r in toRemove)
                {
                    Hotspot.Clients.Remove(r);
                }
                Hotspot.ClientCount = Hotspot.Clients.Count;
                Hotspot.StatusMessage = res.message;

                // Persist Blocked Clients to settings
                _settings.BlockedClients = Hotspot.BlockedClients.ToList();
                _settingsService.SaveSettings(_settings);
            });

            UnblockClientCommand = new RelayCommand<BlockedClientInfo>(async blocked =>
            {
                if (blocked == null) return;
                string cleanMac = HotspotModel.NormalizeMac(blocked.MacAddress);
                var res = await _hotspotService.UnblockClientAsync(blocked.MacAddress, blocked.IpAddress);
                var toRemove = Hotspot.BlockedClients.Where(b => HotspotModel.NormalizeMac(b.MacAddress) == cleanMac).ToList();
                foreach (var b in toRemove)
                {
                    Hotspot.BlockedClients.Remove(b);
                }
                Hotspot.StatusMessage = res.message;

                // Persist Blocked Clients to settings
                _settings.BlockedClients = Hotspot.BlockedClients.ToList();
                _settingsService.SaveSettings(_settings);

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

            ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
            CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);

            SaveSettingsCommand = new RelayCommand(() =>
            {
                _settingsService.SaveSettings(Settings);
                ConnectionStabilizerService.CaptivePortalProbeUrl = Settings.CaptivePortalUrl;
                Settings.DnsStatusMessage = "✓ Settings successfully saved and network conditions applied!";
            });

            ResetSettingsCommand = new RelayCommand(() =>
            {
                Settings = new AppSettingsModel();
                _settingsService.SaveSettings(Settings);
                ConnectionStabilizerService.CaptivePortalProbeUrl = Settings.CaptivePortalUrl;
                Settings.DnsStatusMessage = "✓ Settings reset to default values.";
            });

            TestDohCommand = new RelayCommand(async () =>
            {
                Settings.IsDnsTesting = true;
                Settings.DnsStatusMessage = "Querying DNS-over-HTTPS provider...";
                var res = await _settingsService.TestDohAsync(Settings.CustomDohUrl);
                Settings.DnsStatusMessage = res.message;
                Settings.IsDnsTesting = false;
            });

            ApplyDohToAdapterCommand = new RelayCommand(async () =>
            {
                string adapter = ConnectedInterface?.InterfaceName ?? string.Empty;
                Settings.DnsStatusMessage = "Configuring adapter DNS...";
                var res = await _settingsService.ApplyDnsToAdapterAsync(adapter, Settings.SelectedDnsProvider);
                Settings.DnsStatusMessage = res.message;
            });

            RestoreDhcpDnsCommand = new RelayCommand(async () =>
            {
                string adapter = ConnectedInterface?.InterfaceName ?? string.Empty;
                Settings.DnsStatusMessage = "Restoring DHCP DNS...";
                var res = await _settingsService.RestoreDhcpDnsAsync(adapter);
                Settings.DnsStatusMessage = res.message;
            });

            TestCaptivePortalCommand = new RelayCommand(async () =>
            {
                Settings.IsCaptiveTesting = true;
                Settings.CaptivePortalStatusMessage = "Sending HTTP HEAD captive portal probe...";
                var res = await _settingsService.ProbeCaptivePortalAsync(Settings.CaptivePortalUrl, Settings.ProbeTimeoutMs);
                Settings.CaptivePortalStatusMessage = res.message;
                Settings.IsCaptiveTesting = false;
            });

            TestProxyCommand = new RelayCommand(async () =>
            {
                Settings.IsProxyTesting = true;
                Settings.ProxyStatusMessage = "Testing proxy routing...";
                var res = await _settingsService.TestProxyAsync(Settings);
                Settings.ProxyStatusMessage = res.message;
                Settings.IsProxyTesting = false;
            });

            OpenCaptiveBrowserCommand = new RelayCommand(() =>
            {
                string url = !string.IsNullOrWhiteSpace(Stabilizer.CaptivePortalRedirectUrl)
                    ? Stabilizer.CaptivePortalRedirectUrl
                    : (!string.IsNullOrWhiteSpace(Settings.CaptivePortalUrl) ? Settings.CaptivePortalUrl : "http://www.msftconnecttest.com/connecttest.txt");
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Stabilizer.CaptivePortalNotice = $"Could not launch browser: {ex.Message}";
                }
            });

            SetAnalyzerViewModeCommand = new RelayCommand(param => AnalyzerViewMode = param?.ToString() ?? "Spectrum");

            SetClientQosLimitCommand = new RelayCommand<HotspotClientInfo>(async client =>
            {
                if (client == null) return;
                var res = await _hotspotService.SetClientQosLimitAsync(client.MacAddress, client.IpAddress, client.QosLimit);
                Hotspot.StatusMessage = res.message;
            });

            // Windows Startup Automation: Auto-start Hotspot / Stabilizer if configured
            if (_settings.AutoStartHotspotOnLaunch)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    if (!Hotspot.IsRunning && Hotspot.State == HotspotState.Off)
                    {
                        await ToggleHotspotAsync();
                    }
                });
            }

            if (_settings.AutoStartStabilizerOnLaunch)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    if (!Stabilizer.IsKeepAliveActive)
                    {
                        Application.Current?.Dispatcher?.Invoke(() => ToggleKeepAliveCommand.Execute(null));
                    }
                });
            }

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

                    // Sample RSSI for Signal Strength vs Time Timeline
                    if (ConnectedInterface != null && ConnectedInterface.Signal > 0 && !string.IsNullOrWhiteSpace(ConnectedInterface.Ssid) && !ConnectedInterface.Ssid.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase))
                    {
                        int calcDbm = (ConnectedInterface.Signal / 2) - 100;
                        RssiHistory.Add(new RssiHistoryPoint(calcDbm, ConnectedInterface.Signal, ConnectedInterface.Ssid));
                        while (RssiHistory.Count > 120 || (RssiHistory.Count > 0 && (DateTime.Now - RssiHistory[0].Timestamp).TotalSeconds > 120))
                        {
                            RssiHistory.RemoveAt(0);
                        }
                    }

                    // Update Hotspot live session uptime counter
                    if (Hotspot.IsRunning && Hotspot.StartedAt.HasValue)
                    {
                        var span = DateTime.Now - Hotspot.StartedAt.Value;
                        Hotspot.UptimeText = span.TotalHours >= 1
                            ? $"{(int)span.TotalHours:D2}h {span.Minutes:D2}m {span.Seconds:D2}s"
                            : $"{span.Minutes:D2}m {span.Seconds:D2}s";
                    }
                    else if (Hotspot.IsRunning)
                    {
                        Hotspot.StartedAt = DateTime.Now;
                        Hotspot.UptimeText = "00m 01s";
                    }
                    else
                    {
                        Hotspot.UptimeText = "Stopped";
                    }

                    // If a reset is actively executing, completely suspend health check, watchdog, and refresh
                    if (_isResettingInProgress)
                    {
                        Hotspot.IsResetCountdownActive = false;
                        _noDataCountSeconds = 0;
                        _inTransitionCountSeconds = 0;
                        _unknownStateCountSeconds = 0;
                        return;
                    }

                    // --- Hotspot Health Check & 30s Stuck Reset Countdown ---
                    if (Hotspot.IsResetCountdownActive)
                    {
                        // If data flow is restored on any device during countdown, dismiss countdown automatically
                        if (Hotspot.Clients.Count > 0 && Hotspot.Clients.Any(c => c.IsGettingData))
                        {
                            Hotspot.IsResetCountdownActive = false;
                            _noDataCountSeconds = 0;
                            Hotspot.StatusMessage = $"✓ Data flow restored ({Hotspot.Clients.Count(c => c.IsGettingData)}/{Hotspot.Clients.Count} devices active). Auto-reset dismissed.";
                        }
                        else
                        {
                            Hotspot.ResetCountdownSeconds--;
                            if (Hotspot.ResetCountdownSeconds <= 0)
                            {
                                Hotspot.IsResetCountdownActive = false;
                                _inTransitionCountSeconds = 0;
                                _unknownStateCountSeconds = 0;
                                _noDataCountSeconds = 0;
                                await PerformHotspotResetAsync();
                            }
                            else
                            {
                                int totalDevs = Hotspot.Clients.Count;
                                Hotspot.ResetCountdownMessage = $"⚠️ All {totalDevs} device(s) connected to the hotspot are not getting data. Auto-resetting in {Hotspot.ResetCountdownSeconds}s to restore data flow (Click 'Cancel Reset' to dismiss)...";
                            }
                        }
                    }
                    else
                    {
                        if (_resetCooldownSeconds > 0)
                        {
                            _resetCooldownSeconds--;
                        }
                        else if (Hotspot.IsHealthCheckEnabled && !_isUserActionInProgress && Hotspot.IsRunning && Hotspot.State == HotspotState.On)
                        {
                            int thresholdSec = Math.Max(2, Hotspot.WatchdogIntervalSeconds);

                            // 1. Continuous Connected Client Data Flow Watchdog
                            int totalDevices = Hotspot.Clients.Count;
                            if (totalDevices > 0)
                            {
                                int workingCount = Hotspot.Clients.Count(c => c.IsGettingData);
                                Hotspot.ActiveDataClientCount = workingCount;

                                if (workingCount == 0)
                                {
                                    // All devices connected to the hotspot have 0 data flow!
                                    _noDataCountSeconds++;
                                    if (_noDataCountSeconds >= thresholdSec)
                                    {
                                        Hotspot.IsResetCountdownActive = true;
                                        Hotspot.ResetCountdownSeconds = 30;
                                        Hotspot.ResetCountdownMessage = $"⚠️ All {totalDevices} device(s) connected to the hotspot are not getting data ({thresholdSec}s watchdog check reached). Auto-resetting Hotspot in 30s to restore data flow (Click 'Cancel Reset' to dismiss)...";
                                        _noDataCountSeconds = 0;
                                    }
                                }
                                else
                                {
                                    _noDataCountSeconds = 0;
                                }
                            }
                            else
                            {
                                _noDataCountSeconds = 0;
                            }

                            // 2. Hotspot OS tethering operational state health check
                            if (Hotspot.State == HotspotState.InTransition)
                            {
                                _inTransitionCountSeconds++;
                                if (_inTransitionCountSeconds >= thresholdSec)
                                {
                                    Hotspot.IsResetCountdownActive = true;
                                    Hotspot.ResetCountdownSeconds = 30;
                                    Hotspot.ResetCountdownMessage = $"⚠️ Hotspot is stuck in transition ({thresholdSec}s watchdog check reached). Auto-resetting in 30s...";
                                    _inTransitionCountSeconds = 0;
                                }
                            }
                            else if (Hotspot.State == HotspotState.Unknown && (_wasExplicitlyStarted || Hotspot.IsRunning))
                            {
                                _unknownStateCountSeconds++;
                                if (_unknownStateCountSeconds >= thresholdSec)
                                {
                                    Hotspot.IsResetCountdownActive = true;
                                    Hotspot.ResetCountdownSeconds = 30;
                                    Hotspot.ResetCountdownMessage = $"⚠️ Hotspot service reported unrecoverable error for {thresholdSec}s. Auto-resetting in 30s...";
                                    _unknownStateCountSeconds = 0;
                                }
                            }
                            else
                            {
                                _inTransitionCountSeconds = 0;
                                _unknownStateCountSeconds = 0;
                            }
                        }
                        else if (!Hotspot.IsRunning || !Hotspot.IsHealthCheckEnabled)
                        {
                            _inTransitionCountSeconds = 0;
                            _unknownStateCountSeconds = 0;
                            _noDataCountSeconds = 0;
                        }
                    }

                    // Hotspot auto-refresh and Anti-Idle Keep-Alive watchdog
                    // Poll more frequently (every 1s) when busy/transitioning/countdown, otherwise every 2s
                    _hotspotRefreshCounter++;
                    int refreshThreshold = (Hotspot.State == HotspotState.InTransition || Hotspot.IsBusy || Hotspot.IsResetCountdownActive) ? 1 : 2;
                    if (_hotspotRefreshCounter >= refreshThreshold)
                    {
                        _hotspotRefreshCounter = 0;

                        // Anti-Idle Keep-Alive: if Windows auto-stopped the hotspot while user intended it to run
                        if (Hotspot.KeepHotspotAlive && _wasExplicitlyStarted && !Hotspot.IsBusy && Hotspot.State == HotspotState.Off)
                        {
                            Hotspot.StatusMessage = "⚡ Reactivating Hotspot (Prevented Windows Idle Shutdown)...";
                            await _hotspotService.StartHotspotAsync();
                            await RefreshHotspotAsync();
                        }
                        else if (Hotspot.IsRunning && !Hotspot.IsBusy)
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
            if (Hotspot.IsBusy || _isResettingInProgress) return;
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
                Hotspot.StatusMessage = current.StatusMessage;

                if (string.IsNullOrEmpty(HotspotSsidInput) || HotspotSsidInput == "Wifi_Hotspot")
                {
                    HotspotSsidInput = current.Ssid;
                    HotspotPasswordInput = current.Passphrase;
                    HotspotBandInput = current.Band;
                }

                var currentAllowed = current.Clients
                    .Where(c => !Hotspot.BlockedClients.Any(b => HotspotModel.NormalizeMac(b.MacAddress) == HotspotModel.NormalizeMac(c.MacAddress))
                             && !HotspotService.IsClientBlocked(c.MacAddress)
                             && !HotspotService.IsClientDisconnected(c.MacAddress))
                    .ToList();

                // In-place reconciliation to prevent UI flicker & selection loss
                foreach (var fresh in currentAllowed)
                {
                    string freshMac = HotspotModel.NormalizeMac(fresh.MacAddress);
                    var existing = Hotspot.Clients.FirstOrDefault(c => HotspotModel.NormalizeMac(c.MacAddress) == freshMac);
                    if (existing != null)
                    {
                        if (!string.IsNullOrEmpty(fresh.HostName) && fresh.HostName != "Client Device") existing.HostName = fresh.HostName;
                        if (!string.IsNullOrEmpty(fresh.IpAddress) && fresh.IpAddress != "-") existing.IpAddress = fresh.IpAddress;
                        existing.Status = fresh.Status;
                        existing.ConnectionSpeed = fresh.ConnectionSpeed;
                        existing.LinkSpeed = fresh.LinkSpeed;
                        existing.Latency = fresh.Latency;
                        existing.IsGettingData = fresh.IsGettingData;
                        existing.DataStatusText = fresh.DataStatusText;
                        existing.TotalBytesReceived = fresh.TotalBytesReceived;
                        existing.TotalBytesSent = fresh.TotalBytesSent;
                        existing.TotalDataFormatted = fresh.TotalDataFormatted;
                        existing.DataBreakdown = fresh.DataBreakdown;
                        if (existing.OnQosChanged == null)
                        {
                            existing.OnQosChanged = c => SetClientQosLimitCommand.Execute(c);
                        }
                    }
                    else
                    {
                        fresh.OnQosChanged = c => SetClientQosLimitCommand.Execute(c);
                        Hotspot.Clients.Add(fresh);
                    }
                }

                var freshMacs = new HashSet<string>(currentAllowed.Select(c => HotspotModel.NormalizeMac(c.MacAddress)));
                var toRemove = Hotspot.Clients.Where(c => !freshMacs.Contains(HotspotModel.NormalizeMac(c.MacAddress))).ToList();
                foreach (var stale in toRemove)
                {
                    Hotspot.Clients.Remove(stale);
                }

                Hotspot.ClientCount = Hotspot.Clients.Count;
                Hotspot.ActiveDataClientCount = Hotspot.Clients.Count(c => c.IsGettingData);
                if (Hotspot.ClientCount > 0)
                {
                    if (Hotspot.ActiveDataClientCount == Hotspot.ClientCount)
                        Hotspot.DataHealthStatus = $"All {Hotspot.ClientCount} device(s) getting data";
                    else if (Hotspot.ActiveDataClientCount == 0)
                        Hotspot.DataHealthStatus = $"⚠️ 0/{Hotspot.ClientCount} devices getting data (Stalled)";
                    else
                        Hotspot.DataHealthStatus = $"{Hotspot.ActiveDataClientCount}/{Hotspot.ClientCount} device(s) getting data";
                }
                else
                {
                    Hotspot.DataHealthStatus = "No devices connected";
                }
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
            _isUserActionInProgress = true;
            _inTransitionCountSeconds = 0;
            try
            {
                if (Hotspot.IsRunning)
                {
                    _wasExplicitlyStarted = false;
                    Hotspot.StartedAt = null;
                    Hotspot.StatusMessage = "Stopping Hotspot...";
                    var res = await _hotspotService.StopHotspotAsync();
                    Hotspot.StatusMessage = res.message;
                }
                else
                {
                    Hotspot.StatusMessage = "Creating fresh Hotspot & optimizing DHCP...";
                    // Hotspot creation removes previous settings and creates it as new:
                    Hotspot.Clients.Clear();
                    Hotspot.ClientCount = 0;

                    // Optimize DHCP scope (192.168.137.1)
                    await _hotspotService.OptimizeHotspotDhcpAsync();

                    // Configure fresh as new
                    await _hotspotService.ConfigureHotspotAsync(HotspotSsidInput, HotspotPasswordInput, HotspotChannelChoice);

                    // Start Hotspot
                    var res = await _hotspotService.StartHotspotAsync();
                    Hotspot.StatusMessage = res.message;
                    if (res.success)
                    {
                        _wasExplicitlyStarted = true;
                        Hotspot.StartedAt = DateTime.Now;
                    }
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
                _isUserActionInProgress = false;
                _inTransitionCountSeconds = 0;
            }
        }

        private async Task SaveHotspotConfigAsync()
        {
            Hotspot.IsBusy = true;
            _isUserActionInProgress = true;
            _inTransitionCountSeconds = 0;
            try
            {
                Hotspot.StatusMessage = "Wiping old settings & applying fresh configuration...";
                await _hotspotService.OptimizeHotspotDhcpAsync();
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
                _isUserActionInProgress = false;
                _inTransitionCountSeconds = 0;
            }
        }

        public async Task PerformHotspotResetAsync()
        {
            if (_isResettingInProgress) return; // Prevent duplicate reset loops
            _isResettingInProgress = true;
            Hotspot.IsResetCountdownActive = false; // Guarantee no reset suggestion banner appears during reset
            _inTransitionCountSeconds = 0;
            _unknownStateCountSeconds = 0;
            _noDataCountSeconds = 0;
            Hotspot.IsBusy = true;
            try
            {
                Hotspot.StatusMessage = "🔄 Resetting Hotspot, repairing DHCP scope (192.168.137.1), and virtual adapter...";
                var res = await _hotspotService.ResetAndRecreateHotspotAsync(HotspotSsidInput, HotspotPasswordInput, HotspotChannelChoice);
                Hotspot.StatusMessage = res.message;
                _wasExplicitlyStarted = res.success;
                if (res.success)
                {
                    Hotspot.StartedAt = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Hotspot.StatusMessage = $"Reset error: {ex.Message}";
            }
            finally
            {
                Hotspot.IsBusy = false;
                _isResettingInProgress = false;
                _resetCooldownSeconds = 25; // 25s post-reset grace period for reconnecting clients
                _inTransitionCountSeconds = 0;
                _unknownStateCountSeconds = 0;
                _noDataCountSeconds = 0;
                Hotspot.IsResetCountdownActive = false;
                await RefreshHotspotAsync();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
