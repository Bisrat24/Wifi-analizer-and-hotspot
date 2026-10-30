using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WifiAnalyzerHotspot.Models
{
    public class HeartbeatEntry
    {
        public string Time { get; set; } = string.Empty;
        public double LatencyMs { get; set; }
        public bool IsSuccess { get; set; }
        public string StatusText { get; set; } = "OK";
        public Brush StatusBrush => IsSuccess ? Brushes.LimeGreen : Brushes.Red;
    }

    public class ConnectionStabilizerModel : INotifyPropertyChanged
    {
        // ==================== ACTIVE KEEP-ALIVE ====================
        private bool _isKeepAliveActive;
        private int _keepAliveIntervalSeconds = 5;
        private string _keepAliveTarget = "1.1.1.1";
        private string _keepAliveMethod = "ICMP Ping";
        private long _packetsSent;
        private long _packetsSuccess;
        private long _packetsLost;
        private double _currentLatencyMs;
        private double _avgLatencyMs;
        private string _keepAliveStatusText = "Keep-Alive is Stopped";
        private bool _isPulseGlowing;

        public bool IsKeepAliveActive
        {
            get => _isKeepAliveActive;
            set
            {
                _isKeepAliveActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(KeepAliveButtonText));
                OnPropertyChanged(nameof(KeepAliveStatusBrush));
            }
        }

        public string KeepAliveButtonText => _isKeepAliveActive ? "TURN OFF KEEP-ALIVE" : "START KEEP-ALIVE";
        public Brush KeepAliveStatusBrush => _isKeepAliveActive ? Brushes.LimeGreen : Brushes.Gray;

        public int KeepAliveIntervalSeconds
        {
            get => _keepAliveIntervalSeconds;
            set { _keepAliveIntervalSeconds = Math.Clamp(value, 1, 60); OnPropertyChanged(); }
        }

        public string KeepAliveIntervalChoice
        {
            get => $"{_keepAliveIntervalSeconds}s";
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                if (value.StartsWith("1 second")) KeepAliveIntervalSeconds = 1;
                else if (value.StartsWith("3 seconds")) KeepAliveIntervalSeconds = 3;
                else if (value.StartsWith("5 seconds")) KeepAliveIntervalSeconds = 5;
                else if (value.StartsWith("10 seconds")) KeepAliveIntervalSeconds = 10;
                else if (value.StartsWith("15 seconds")) KeepAliveIntervalSeconds = 15;
                OnPropertyChanged();
            }
        }

        public string KeepAliveTarget
        {
            get => _keepAliveTarget;
            set { _keepAliveTarget = value; OnPropertyChanged(); }
        }

        public string KeepAliveMethod
        {
            get => _keepAliveMethod;
            set { _keepAliveMethod = value; OnPropertyChanged(); }
        }

        public long PacketsSent
        {
            get => _packetsSent;
            set
            {
                _packetsSent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PacketLossPercent));
            }
        }

        public long PacketsSuccess
        {
            get => _packetsSuccess;
            set
            {
                _packetsSuccess = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PacketLossPercent));
            }
        }

        public long PacketsLost
        {
            get => _packetsLost;
            set
            {
                _packetsLost = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PacketLossPercent));
            }
        }

        public double CurrentLatencyMs
        {
            get => _currentLatencyMs;
            set { _currentLatencyMs = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public double AvgLatencyMs
        {
            get => _avgLatencyMs;
            set { _avgLatencyMs = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public double PacketLossPercent
        {
            get
            {
                if (_packetsSent <= 0) return 0.0;
                return Math.Round((double)_packetsLost / _packetsSent * 100.0, 1);
            }
        }

        public string KeepAliveStatusText
        {
            get => _keepAliveStatusText;
            set { _keepAliveStatusText = value; OnPropertyChanged(); }
        }

        public bool IsPulseGlowing
        {
            get => _isPulseGlowing;
            set { _isPulseGlowing = value; OnPropertyChanged(); }
        }

        public ObservableCollection<HeartbeatEntry> HeartbeatHistory { get; set; } = new();

        // ==================== CAPTIVE PORTAL DETECTION ====================
        private bool _isCaptivePortalDetected;
        private string _captivePortalRedirectUrl = string.Empty;
        private string _captivePortalNotice = string.Empty;

        public bool IsCaptivePortalDetected
        {
            get => _isCaptivePortalDetected;
            set { _isCaptivePortalDetected = value; OnPropertyChanged(); }
        }

        public string CaptivePortalRedirectUrl
        {
            get => _captivePortalRedirectUrl;
            set { _captivePortalRedirectUrl = value; OnPropertyChanged(); }
        }

        public string CaptivePortalNotice
        {
            get => _captivePortalNotice;
            set { _captivePortalNotice = value; OnPropertyChanged(); }
        }

        // ==================== ACTIVE RECONNECT ====================
        private bool _isReconnectActive;
        private string _targetSsid = "Africa";
        private int _failureThreshold = 3;
        private string _reconnectStrategy = "Fast Wi-Fi Re-auth";
        private int _totalDropsDetected;
        private int _totalReconnected;
        private string _reconnectStatusText = "Active Reconnect is Idle";
        private string _lastReconnectTime = "-";

        public bool IsReconnectActive
        {
            get => _isReconnectActive;
            set
            {
                _isReconnectActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReconnectButtonText));
                OnPropertyChanged(nameof(ReconnectStatusBrush));
            }
        }

        public string ReconnectButtonText => _isReconnectActive ? "TURN OFF RECONNECT" : "START ACTIVE RECONNECT";
        public Brush ReconnectStatusBrush => _isReconnectActive ? Brushes.LimeGreen : Brushes.Gray;

        public string TargetSsid
        {
            get => _targetSsid;
            set { _targetSsid = value; OnPropertyChanged(); }
        }

        public int FailureThreshold
        {
            get => _failureThreshold;
            set { _failureThreshold = Math.Clamp(value, 1, 10); OnPropertyChanged(); }
        }

        public string FailureThresholdChoice
        {
            get => $"{_failureThreshold} drops";
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                if (value.StartsWith("2")) FailureThreshold = 2;
                else if (value.StartsWith("3")) FailureThreshold = 3;
                else if (value.StartsWith("5")) FailureThreshold = 5;
                OnPropertyChanged();
            }
        }

        private int _reconnectBufferSeconds = 10;
        public int ReconnectBufferSeconds
        {
            get => _reconnectBufferSeconds;
            set { _reconnectBufferSeconds = Math.Clamp(value, 3, 60); OnPropertyChanged(); }
        }

        public string ReconnectBufferChoice
        {
            get => $"{_reconnectBufferSeconds} seconds";
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                if (value.StartsWith("5")) ReconnectBufferSeconds = 5;
                else if (value.StartsWith("10")) ReconnectBufferSeconds = 10;
                else if (value.StartsWith("15")) ReconnectBufferSeconds = 15;
                else if (value.StartsWith("25")) ReconnectBufferSeconds = 25;
                OnPropertyChanged();
            }
        }

        public string ReconnectStrategy
        {
            get => _reconnectStrategy;
            set { _reconnectStrategy = value; OnPropertyChanged(); }
        }

        public int TotalDropsDetected
        {
            get => _totalDropsDetected;
            set { _totalDropsDetected = value; OnPropertyChanged(); }
        }

        public int TotalReconnected
        {
            get => _totalReconnected;
            set { _totalReconnected = value; OnPropertyChanged(); }
        }

        public string ReconnectStatusText
        {
            get => _reconnectStatusText;
            set { _reconnectStatusText = value; OnPropertyChanged(); }
        }

        public string LastReconnectTime
        {
            get => _lastReconnectTime;
            set { _lastReconnectTime = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> ReconnectLogs { get; set; } = new();

        public List<string> LogLimitOptions { get; } = new() { "50 entries", "100 entries", "300 entries" };

        private string _keepAliveLogLimitChoice = "100 entries";
        private int _keepAliveLogLimit = 100;

        public string KeepAliveLogLimitChoice
        {
            get => _keepAliveLogLimitChoice;
            set
            {
                _keepAliveLogLimitChoice = value;
                OnPropertyChanged();
                var match = System.Text.RegularExpressions.Regex.Match(value ?? "", @"^(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int limit))
                {
                    _keepAliveLogLimit = limit;
                    while (HeartbeatHistory.Count > _keepAliveLogLimit)
                    {
                        HeartbeatHistory.RemoveAt(HeartbeatHistory.Count - 1);
                    }
                }
                OnPropertyChanged(nameof(KeepAliveLogLimit));
            }
        }

        public int KeepAliveLogLimit
        {
            get => _keepAliveLogLimit;
            set
            {
                _keepAliveLogLimit = value > 0 ? value : 100;
                _keepAliveLogLimitChoice = $"{_keepAliveLogLimit} entries";
                while (HeartbeatHistory.Count > _keepAliveLogLimit)
                {
                    HeartbeatHistory.RemoveAt(HeartbeatHistory.Count - 1);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(KeepAliveLogLimitChoice));
            }
        }

        private string _reconnectLogLimitChoice = "100 entries";
        private int _reconnectLogLimit = 100;

        public string ReconnectLogLimitChoice
        {
            get => _reconnectLogLimitChoice;
            set
            {
                _reconnectLogLimitChoice = value;
                OnPropertyChanged();
                var match = System.Text.RegularExpressions.Regex.Match(value ?? "", @"^(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int limit))
                {
                    _reconnectLogLimit = limit;
                    while (ReconnectLogs.Count > _reconnectLogLimit)
                    {
                        ReconnectLogs.RemoveAt(ReconnectLogs.Count - 1);
                    }
                }
                OnPropertyChanged(nameof(ReconnectLogLimit));
            }
        }

        public int ReconnectLogLimit
        {
            get => _reconnectLogLimit;
            set
            {
                _reconnectLogLimit = value > 0 ? value : 100;
                _reconnectLogLimitChoice = $"{_reconnectLogLimit} entries";
                while (ReconnectLogs.Count > _reconnectLogLimit)
                {
                    ReconnectLogs.RemoveAt(ReconnectLogs.Count - 1);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReconnectLogLimitChoice));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
