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
        private int _resetCountdownSeconds = 30;
        private string _resetCountdownMessage = string.Empty;
        private bool _isHealthCheckEnabled = true;
        private string _dhcpPoolInfo = "192.168.137.1 (ICS Subnet 255.255.255.0)";
        private int _activeDataClientCount = 0;
        private string _dataHealthStatus = "Ready";
        private bool _isHighestPriorityMode = true;
        private string _priorityStatusText = "⚡ Highest PC Priority: Enabled";
        private string _priorityDetails = "High CPU Priority • Max Radio Power • No Sleep Throttling • Zero Network Throttling";
        public DateTime? StartedAt { get; set; }

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
}
