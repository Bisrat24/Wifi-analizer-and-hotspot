using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WifiAnalyzerHotspot.Models
{
    public class SavedWifiProfile : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _ssid = string.Empty;
        private string _authentication = "WPA2-Personal";
        private bool _isAutoConnect = true;
        private bool _isAvailable = false;
        private int _signalPercent = 0;
        private bool _isConnected = false;
        private DateTime? _lastConnected;
        private string? _password;
        private bool _isPasswordRevealed;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Ssid
        {
            get => string.IsNullOrWhiteSpace(_ssid) ? _name : _ssid;
            set { _ssid = value; OnPropertyChanged(); }
        }

        public string Authentication
        {
            get => _authentication;
            set { _authentication = value; OnPropertyChanged(); }
        }

        public bool IsAutoConnect
        {
            get => _isAutoConnect;
            set { _isAutoConnect = value; OnPropertyChanged(); }
        }

        public bool IsAvailable
        {
            get => _isAvailable;
            set { _isAvailable = value; OnPropertyChanged(); }
        }

        public int SignalPercent
        {
            get => _signalPercent;
            set { _signalPercent = value; OnPropertyChanged(); }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        public DateTime? LastConnected
        {
            get => _lastConnected;
            set
            {
                _lastConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastConnectedText));
            }
        }

        public string LastConnectedText => _lastConnected.HasValue
            ? _lastConnected.Value.ToString("yyyy-MM-dd HH:mm")
            : "Saved";

        public string? Password
        {
            get => _password;
            set { _password = value; OnPropertyChanged(); }
        }

        public bool IsPasswordRevealed
        {
            get => _isPasswordRevealed;
            set { _isPasswordRevealed = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
