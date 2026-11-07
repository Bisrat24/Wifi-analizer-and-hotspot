using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WifiAnalyzerHotspot.Models
{
    public class HotspotClientInfo : INotifyPropertyChanged
    {
        private string _hostName = "Client Device";
        private string _macAddress = string.Empty;
        private string _ipAddress = string.Empty;
        private string _status = "Connected";
        private string _connectionSpeed = "Detecting...";
        private string _linkSpeed = "-";
        private string _latency = "-";
        private string _trafficRate = "-";
        private bool _isGettingData = true;
        private string _dataStatusText = "Checking Data...";

        // Cumulative Data Usage per Device
        private long _totalBytesReceived = 0;
        private long _totalBytesSent = 0;
        private string _totalDataFormatted = "0.0 MB";
        private string _dataBreakdown = "↓ 0.0 MB • ↑ 0.0 MB";

        // Bandwidth Throttling / QoS
        private string _qosLimit = "Unlimited";
        private bool _isQosThrottled = false;

        // Client Priority: 1 (Highest) to 7 (Least), default = 5
        private int _priority = 5;
        public Action<HotspotClientInfo>? OnPriorityChanged { get; set; }

        public int Priority
        {
            get => _priority;
            set
            {
                int clamped = Math.Clamp(value, 1, 7);
                if (_priority != clamped)
                {
                    _priority = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PriorityLabel));
                    OnPropertyChanged(nameof(PriorityDescription));
                    OnPropertyChanged(nameof(IsHighPriority));
                    OnPriorityChanged?.Invoke(this);
                }
            }
        }

        public bool IsHighPriority => _priority <= 3;

        public string PriorityLabel => _priority switch
        {
            1 => "1 - Highest",
            2 => "2 - High",
            3 => "3 - Elevated",
            4 => "4 - Normal",
            5 => "5 - Default",
            6 => "6 - Low",
            7 => "7 - Lowest",
            _ => $"{_priority}"
        };

        public string PriorityDescription => _priority switch
        {
            1 => "Priority 1 (Highest): Max throughput, 802.1p Network Control, 0% latency queue",
            2 => "Priority 2 (High): Voice & interactive audio prioritized",
            3 => "Priority 3 (Elevated): Low-latency streaming & video prioritized",
            4 => "Priority 4 (Normal): Standard accelerated traffic",
            5 => "Priority 5 (Default): Standard Best Effort general networking",
            6 => "Priority 6 (Low): Deprioritized background traffic",
            7 => "Priority 7 (Lowest): Scavenger class, lowest network queue share",
            _ => $"Priority {_priority}"
        };

        public long TotalBytesReceived
        {
            get => _totalBytesReceived;
            set { _totalBytesReceived = value; OnPropertyChanged(); }
        }

        public long TotalBytesSent
        {
            get => _totalBytesSent;
            set { _totalBytesSent = value; OnPropertyChanged(); }
        }

        public string TotalDataFormatted
        {
            get => _totalDataFormatted;
            set { _totalDataFormatted = value; OnPropertyChanged(); }
        }

        public string DataBreakdown
        {
            get => _dataBreakdown;
            set { _dataBreakdown = value; OnPropertyChanged(); }
        }

        public Action<HotspotClientInfo>? OnQosChanged { get; set; }

        public string QosLimit
        {
            get => _qosLimit;
            set
            {
                if (_qosLimit != value)
                {
                    _qosLimit = value;
                    _isQosThrottled = !string.IsNullOrEmpty(value) && !value.Equals("Unlimited", StringComparison.OrdinalIgnoreCase);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsQosThrottled));
                    OnQosChanged?.Invoke(this);
                }
            }
        }

        public bool IsQosThrottled
        {
            get => _isQosThrottled;
            set { _isQosThrottled = value; OnPropertyChanged(); }
        }

        public string HostName
        {
            get => _hostName;
            set { _hostName = value; OnPropertyChanged(); }
        }

        public string MacAddress
        {
            get => _macAddress;
            set { _macAddress = value; OnPropertyChanged(); }
        }

        public string IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public bool IsGettingData
        {
            get => _isGettingData;
            set { _isGettingData = value; OnPropertyChanged(); }
        }

        public string DataStatusText
        {
            get => _dataStatusText;
            set { _dataStatusText = value; OnPropertyChanged(); }
        }

        public string ConnectionSpeed
        {
            get => _connectionSpeed;
            set { _connectionSpeed = value; OnPropertyChanged(); }
        }

        public string LinkSpeed
        {
            get => _linkSpeed;
            set { _linkSpeed = value; OnPropertyChanged(); }
        }

        public string Latency
        {
            get => _latency;
            set { _latency = value; OnPropertyChanged(); }
        }

        public string TrafficRate
        {
            get => _trafficRate;
            set { _trafficRate = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public enum HotspotState
    {
        Off,
        On,
        InTransition,
        Unknown
    }

    public class HotspotModel : INotifyPropertyChanged
    {
        private string _ssid = "MyHotspot";
        private string _passphrase = "password123";
        private string _band = "2.4 GHz";
        private string _preferredChannel = "Auto (Recommended)";
        private string _activeChannel = "Auto";
        private string _activeBand = "2.4 GHz";
        private string _channelDescription = "Broadcasting channel synchronizes with active Wi-Fi";
        private HotspotState _state = HotspotState.Off;
        private int _clientCount = 0;
        private string _statusMessage = "Hotspot is stopped";
        private bool _isBusy;
        private bool _keepHotspotAlive = true;
        private string _uptimeText = "Stopped";
        private bool _isResetCountdownActive;
        private int _resetCountdownSeconds = 15;
        private string _resetCountdownMessage = string.Empty;
        private bool _isHealthCheckEnabled = true;
        private string _dhcpPoolInfo = "192.168.137.1 (ICS Subnet 255.255.255.0)";
        private int _activeDataClientCount = 0;
        private string _dataHealthStatus = "Ready";
        private bool _isHighestPriorityMode = true;
        private string _priorityStatusText = "⚡ Highest PC Priority: REALTIME";
        private string _priorityDetails = "RealTime CPU Priority • Max Radio Power • No Sleep Throttling • Zero Network Throttling";
        public DateTime? StartedAt { get; set; }

        private string _watchdogIntervalChoice = "5 seconds (Recommended)";
        private int _watchdogIntervalSeconds = 5;

        public List<string> WatchdogIntervalOptions { get; } = new()
        {
            "2 seconds (Ultra-Fast)",
            "3 seconds (Aggressive)",
            "5 seconds (Recommended)",
            "10 seconds (Standard)",
            "15 seconds (Relaxed)",
            "30 seconds (High Tolerance)"
        };

        public string WatchdogIntervalChoice
        {
            get => _watchdogIntervalChoice;
            set
            {
                _watchdogIntervalChoice = value;
                OnPropertyChanged();
                var match = System.Text.RegularExpressions.Regex.Match(value ?? "", @"^(\d+)\s*sec");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int sec))
                {
                    _watchdogIntervalSeconds = sec;
                }
                else
                {
                    _watchdogIntervalSeconds = 5;
                }
                OnPropertyChanged(nameof(WatchdogIntervalSeconds));
            }
        }

        public int WatchdogIntervalSeconds
        {
            get => _watchdogIntervalSeconds;
            set
            {
                _watchdogIntervalSeconds = Math.Max(1, value);
                var match = WatchdogIntervalOptions.Find(o => o.StartsWith($"{_watchdogIntervalSeconds} sec", StringComparison.OrdinalIgnoreCase));
                if (match != null) _watchdogIntervalChoice = match;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WatchdogIntervalChoice));
            }
        }

        public bool IsHighestPriorityMode
        {
            get => _isHighestPriorityMode;
            set { _isHighestPriorityMode = value; OnPropertyChanged(); }
        }

        public string PriorityStatusText
        {
            get => _priorityStatusText;
            set { _priorityStatusText = value; OnPropertyChanged(); }
        }

        public string PriorityDetails
        {
            get => _priorityDetails;
            set { _priorityDetails = value; OnPropertyChanged(); }
        }

        public bool KeepHotspotAlive
        {
            get => _keepHotspotAlive;
            set { _keepHotspotAlive = value; OnPropertyChanged(); }
        }

        public string UptimeText
        {
            get => _uptimeText;
            set { _uptimeText = value; OnPropertyChanged(); }
        }

        public bool IsResetCountdownActive
        {
            get => _isResetCountdownActive;
            set { _isResetCountdownActive = value; OnPropertyChanged(); }
        }

        public int ResetCountdownSeconds
        {
            get => _resetCountdownSeconds;
            set { _resetCountdownSeconds = value; OnPropertyChanged(); }
        }

        public string ResetCountdownMessage
        {
            get => _resetCountdownMessage;
            set { _resetCountdownMessage = value; OnPropertyChanged(); }
        }

        public bool IsHealthCheckEnabled
        {
            get => _isHealthCheckEnabled;
            set { _isHealthCheckEnabled = value; OnPropertyChanged(); }
        }

        public string DhcpPoolInfo
        {
            get => _dhcpPoolInfo;
            set { _dhcpPoolInfo = value; OnPropertyChanged(); }
        }

        public int ActiveDataClientCount
        {
            get => _activeDataClientCount;
            set { _activeDataClientCount = value; OnPropertyChanged(); }
        }

        public string DataHealthStatus
        {
            get => _dataHealthStatus;
            set { _dataHealthStatus = value; OnPropertyChanged(); }
        }

        public string Ssid
        {
            get => _ssid;
            set { _ssid = value; OnPropertyChanged(); }
        }

        public string Passphrase
        {
            get => _passphrase;
            set { _passphrase = value; OnPropertyChanged(); }
        }

        public string Band
        {
            get => _band;
            set { _band = value; OnPropertyChanged(); }
        }

        public string PreferredChannel
        {
            get => _preferredChannel;
            set { _preferredChannel = value; OnPropertyChanged(); }
        }

        public string ActiveChannel
        {
            get => _activeChannel;
            set { _activeChannel = value; OnPropertyChanged(); }
        }

        public string ActiveBand
        {
            get => _activeBand;
            set { _activeBand = value; OnPropertyChanged(); }
        }

        public string ChannelDescription
        {
            get => _channelDescription;
            set { _channelDescription = value; OnPropertyChanged(); }
        }

        public HotspotState State
        {
            get => _state;
            set
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(PowerButtonText));
            }
        }

        public bool IsRunning => _state == HotspotState.On;

        public string StateText => _state switch
        {
            HotspotState.On => "Active",
            HotspotState.Off => "Stopped",
            HotspotState.InTransition => "Switching...",
            _ => "Unknown"
        };

        public string PowerButtonText => _state switch
        {
            HotspotState.On => "TURN OFF",
            HotspotState.InTransition => "WAIT...",
            _ => "TURN ON"
        };

        public int ClientCount
        {
            get => _clientCount;
            set { _clientCount = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); }
        }

        public ObservableCollection<HotspotClientInfo> Clients { get; set; } = new();
        public ObservableCollection<BlockedClientInfo> BlockedClients { get; set; } = new();
        public ObservableCollection<string> ResetLogs { get; set; } = new();

        public void AddResetLog(string message)
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                ResetLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
                while (ResetLogs.Count > 100)
                {
                    ResetLogs.RemoveAt(ResetLogs.Count - 1);
                }
            });
        }

        public static string NormalizeMac(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return string.Empty;
            return mac.Replace(":", "").Replace("-", "").Replace(".", "").Trim().ToUpperInvariant();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class BlockedClientInfo : INotifyPropertyChanged
    {
        private string _hostName = "Blocked Device";
        private string _macAddress = string.Empty;
        private string _ipAddress = string.Empty;
        private string _blockedTime = string.Empty;
        private string _reason = "Blocked by Administrator";

        public string HostName
        {
            get => _hostName;
            set { _hostName = value; OnPropertyChanged(); }
        }

        public string MacAddress
        {
            get => _macAddress;
            set { _macAddress = value; OnPropertyChanged(); }
        }

        public string IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnPropertyChanged(); }
        }

        public string BlockedTime
        {
            get => _blockedTime;
            set { _blockedTime = value; OnPropertyChanged(); }
        }

        public string Reason
        {
            get => _reason;
            set { _reason = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ClientPriorityOption
    {
        public int Value { get; set; }
        public string DisplayText { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string BadgeColor { get; set; } = "#00E5FF";
        public override string ToString() => DisplayText;
    }
}
