using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WifiAnalyzerHotspot.Models
{
    public enum SpeedTestStage
    {
        Idle,
        Ping,
        Download,
        Upload,
        Completed,
        Failed
    }

    public class SpeedTestMetrics : INotifyPropertyChanged
    {
        private SpeedTestStage _stage = SpeedTestStage.Idle;
        private double _pingMs;
        private double _jitterMs;
        private double _downloadMbps;
        private double _uploadMbps;
        private double _progress;
        private string _statusMessage = "Ready to test";
        private double _gaugeValue; // for needle animation

        public SpeedTestStage Stage
        {
            get => _stage;
            set
            {
                _stage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTesting));
            }
        }

        public bool IsTesting => _stage != SpeedTestStage.Idle && _stage != SpeedTestStage.Completed && _stage != SpeedTestStage.Failed;

        public double PingMs
        {
            get => _pingMs;
            set { _pingMs = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public double JitterMs
        {
            get => _jitterMs;
            set { _jitterMs = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public double DownloadMbps
        {
            get => _downloadMbps;
            set { _downloadMbps = Math.Round(value, 2); OnPropertyChanged(); }
        }

        public double UploadMbps
        {
            get => _uploadMbps;
            set { _uploadMbps = Math.Round(value, 2); OnPropertyChanged(); }
        }

        public double Progress
        {
            get => _progress;
            set { _progress = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public double GaugeValue
        {
            get => _gaugeValue;
            set { _gaugeValue = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class LiveTrafficPoint
    {
        public double DownloadBps { get; set; }
        public double UploadBps { get; set; }
        public string DownloadFormatted { get; set; } = "0 KB/s";
        public string UploadFormatted { get; set; } = "0 KB/s";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class ConnectedInterfaceInfo : INotifyPropertyChanged
    {
        private string _interfaceName = "WiFi";
        private string _description = "Intel Wi-Fi";
        private string _ssid = "Not Connected";
        private string _bssid = "-";
        private string _band = "-";
        private int _channel = 0;
        private string _radioType = "-";
        private string _auth = "-";
        private int _signal = 0;
        private int _rssi = -100;
        private double _rxRateMbps = 0;
        private double _txRateMbps = 0;
        private string _ipv4 = "-";
        private string _gateway = "-";
        private string _mac = "-";

        public string InterfaceName
        {
            get => _interfaceName;
            set { _interfaceName = value; OnPropertyChanged(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        public string Ssid
        {
            get => _ssid;
            set { _ssid = value; OnPropertyChanged(); }
        }

        public string Bssid
        {
            get => _bssid;
            set { _bssid = value; OnPropertyChanged(); }
        }

        public string Band
        {
            get => _band;
            set { _band = value; OnPropertyChanged(); }
        }

        public int Channel
        {
            get => _channel;
            set { _channel = value; OnPropertyChanged(); }
        }

        public string RadioType
        {
            get => _radioType;
            set { _radioType = value; OnPropertyChanged(); }
        }

        public string Auth
        {
            get => _auth;
            set { _auth = value; OnPropertyChanged(); }
        }

        public int Signal
        {
            get => _signal;
            set { _signal = value; OnPropertyChanged(); }
        }

        public int Rssi
        {
            get => _rssi;
            set { _rssi = value; OnPropertyChanged(); }
        }

        public double RxRateMbps
        {
            get => _rxRateMbps;
            set { _rxRateMbps = value; OnPropertyChanged(); }
        }

        public double TxRateMbps
        {
            get => _txRateMbps;
            set { _txRateMbps = value; OnPropertyChanged(); }
        }

        public string Ipv4
        {
            get => _ipv4;
            set { _ipv4 = value; OnPropertyChanged(); }
        }

        public string Gateway
        {
            get => _gateway;
            set { _gateway = value; OnPropertyChanged(); }
        }

        public string Mac
        {
            get => _mac;
            set { _mac = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Bucketed latency distribution bin for real-time packet round-trip time (RTT) histogram.
    /// </summary>
    public class RttHistogramBin : INotifyPropertyChanged
    {
        private string _label = string.Empty;
        private string _rangeText = string.Empty;
        private int _count = 0;
        private double _percentage = 0;
        private string _colorHex = "#00E5FF";

        public string Label
        {
            get => _label;
            set { _label = value; OnPropertyChanged(); }
        }

        public string RangeText
        {
            get => _rangeText;
            set { _rangeText = value; OnPropertyChanged(); }
        }

        public int Count
        {
            get => _count;
            set
            {
                _count = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CountText));
            }
        }

        public double Percentage
        {
            get => _percentage;
            set
            {
                _percentage = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentageText));
            }
        }

        public string CountText => $"{_count} pkts";
        public string PercentageText => $"{_percentage:F0}%";

        public string ColorHex
        {
            get => _colorHex;
            set
            {
                _colorHex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ColorBrush));
            }
        }

        public Brush ColorBrush
        {
            get
            {
                try
                {
                    var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_colorHex));
                    b.Freeze();
                    return b;
                }
                catch
                {
                    return Brushes.Cyan;
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Continuous real-time packet delay variation (jitter) and RTT diagnostics.
    /// Uses RFC 3550 exponential moving average: J = J + (|D(i-1, i)| - J) / 16.0
    /// </summary>
    public class LiveJitterDiagnostics : INotifyPropertyChanged
    {
        private double _currentRttMs = 0;
        private double _jitterMs = 0;
        private double _minRttMs = 0;
        private double _maxRttMs = 0;
        private double _avgRttMs = 0;
        private double _packetLossPercent = 0;
        private int _totalSamples = 0;
        private string _qualityRating = "WAITING";
        private string _qualityColorHex = "#7C88A8";
        private string _targetGateway = "Default Gateway (ICMP / ARP)";
        private string _statusDetail = "Continuously sampling live round-trip time...";

        public double CurrentRttMs
        {
            get => _currentRttMs;
            set { _currentRttMs = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public double JitterMs
        {
            get => _jitterMs;
            set { _jitterMs = Math.Round(value, 2); OnPropertyChanged(); }
        }

        public double MinRttMs
        {
            get => _minRttMs;
            set { _minRttMs = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public double MaxRttMs
        {
            get => _maxRttMs;
            set { _maxRttMs = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public double AvgRttMs
        {
            get => _avgRttMs;
            set { _avgRttMs = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public double PacketLossPercent
        {
            get => _packetLossPercent;
            set { _packetLossPercent = Math.Round(value, 1); OnPropertyChanged(); }
        }

        public int TotalSamples
        {
            get => _totalSamples;
            set { _totalSamples = value; OnPropertyChanged(); }
        }

        public string QualityRating
        {
            get => _qualityRating;
            set { _qualityRating = value; OnPropertyChanged(); }
        }

        public string QualityColorHex
        {
            get => _qualityColorHex;
            set
            {
                _qualityColorHex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(QualityBrush));
            }
        }

        public Brush QualityBrush
        {
            get
            {
                try
                {
                    var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_qualityColorHex));
                    b.Freeze();
                    return b;
                }
                catch
                {
                    return Brushes.Gray;
                }
            }
        }

        public string TargetGateway
        {
            get => _targetGateway;
            set { _targetGateway = value; OnPropertyChanged(); }
        }

        public string StatusDetail
        {
            get => _statusDetail;
            set { _statusDetail = value; OnPropertyChanged(); }
        }

        public void UpdateQuality()
        {
            if (_totalSamples == 0)
            {
                QualityRating = "INITIALIZING";
                QualityColorHex = "#7C88A8";
                return;
            }

            if (_packetLossPercent > 5.0 || _currentRttMs > 100 || _jitterMs > 25)
            {
                QualityRating = "POOR (Lag Spike)";
                QualityColorHex = "#FF5252";
            }
            else if (_jitterMs > 10 || _currentRttMs > 55)
            {
                QualityRating = "MODERATE (Jitter)";
                QualityColorHex = "#FFD54F";
            }
            else if (_jitterMs > 4 || _currentRttMs > 30)
            {
                QualityRating = "GOOD";
                QualityColorHex = "#40C4FF";
            }
            else if (_jitterMs > 1.5 || _currentRttMs > 12)
            {
                QualityRating = "VERY GOOD";
                QualityColorHex = "#00E5FF";
            }
            else
            {
                QualityRating = "EXCELLENT (Zero-Jitter)";
                QualityColorHex = "#00E676";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
