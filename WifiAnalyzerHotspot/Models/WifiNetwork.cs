using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WifiAnalyzerHotspot.Models
{
    public class WifiNetwork : INotifyPropertyChanged
    {
        private static readonly string[] NeonHexes = new[]
        {
            "#00E5FF", // Cyan
            "#FF1744", // Neon Red
            "#00E676", // Neon Green
            "#FFEA00", // Bright Yellow
            "#D500F9", // Neon Purple
            "#FF9100", // Neon Orange
            "#00B0FF", // Light Blue
            "#76FF03", // Lime
            "#F50057", // Hot Pink
            "#1DE9B6", // Teal
            "#FF3D00", // Deep Orange
            "#651FFF"  // Electric Violet
        };

        // Static frozen brushes - completely thread-safe across UI and background threads
        public static readonly SolidColorBrush[] FrozenPalette;

        static WifiNetwork()
        {
            FrozenPalette = new SolidColorBrush[NeonHexes.Length];
            for (int i = 0; i < NeonHexes.Length; i++)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(NeonHexes[i]));
                brush.Freeze(); // Crucial for multi-threaded WPF data binding!
                FrozenPalette[i] = brush;
            }
        }

        private string _ssid = string.Empty;
        private string _bssid = string.Empty;
        private int _signalPercent;
        private int _rssi = -100;
        private string _band = "2.4 GHz";
        private int _channel = 1;
        private int _frequencyMHz = 2412;
        private int _channelWidthMHz = 20;
        private string _radioType = "802.11n";
        private string _authentication = "WPA2-Personal";
        private string _encryption = "CCMP";
        private bool _isConnected;
        private int _colorIndex = 0;

        public string Ssid
        {
            get => string.IsNullOrWhiteSpace(_ssid) ? "[Hidden Network]" : _ssid;
            set { _ssid = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string DisplayName => Ssid;

        public string Bssid
        {
            get => _bssid;
            set { _bssid = value; OnPropertyChanged(); }
        }

        public int SignalPercent
        {
            get => _signalPercent;
            set
            {
                _signalPercent = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
                OnPropertyChanged(nameof(SignalBars));
                OnPropertyChanged(nameof(SignalQuality));
            }
        }

        public int Rssi
        {
            get => _rssi;
            set { _rssi = value; OnPropertyChanged(); }
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

        public int FrequencyMHz
        {
            get => _frequencyMHz;
            set { _frequencyMHz = value; OnPropertyChanged(); }
        }

        public int ChannelWidthMHz
        {
            get => _channelWidthMHz;
            set { _channelWidthMHz = value; OnPropertyChanged(); }
        }

        public string RadioType
        {
            get => _radioType;
            set { _radioType = value; OnPropertyChanged(); }
        }

        public string Authentication
        {
            get => _authentication;
            set { _authentication = value; OnPropertyChanged(); }
        }

        public string Encryption
        {
            get => _encryption;
            set { _encryption = value; OnPropertyChanged(); }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        public int ColorIndex
        {
            get => _colorIndex;
            set
            {
                _colorIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ColorBrush));
            }
        }

        // Returns a thread-safe, frozen SolidColorBrush
        public SolidColorBrush ColorBrush => FrozenPalette[Math.Abs(_colorIndex) % FrozenPalette.Length];

        public int SignalBars
        {
            get
            {
                if (SignalPercent >= 80) return 4;
                if (SignalPercent >= 55) return 3;
                if (SignalPercent >= 30) return 2;
                return 1;
            }
        }

        public string SignalQuality
        {
            get
            {
                if (SignalPercent >= 80) return "Excellent";
                if (SignalPercent >= 60) return "Good";
                if (SignalPercent >= 40) return "Fair";
                return "Weak";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
