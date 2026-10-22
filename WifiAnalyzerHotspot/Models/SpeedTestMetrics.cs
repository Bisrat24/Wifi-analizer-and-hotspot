using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
}
