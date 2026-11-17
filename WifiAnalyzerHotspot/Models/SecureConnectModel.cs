using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WifiAnalyzerHotspot.Models
{
    public class SecureConnectModel : INotifyPropertyChanged
    {
        private bool _isMasterEnabled = false;
        private bool _isFullPowerActive = false;
        private bool _isScanning = false;
        private bool _isConnecting = false;
        private string _statusMessage = "Secure Connect is Idle (OFF)";
        private string _connectionState = "Disconnected";
        private string _connectedSsid = "Disconnected";
        private string _connectedBssid = "";
        private int _connectedSignal = 0;
        private string _connectedSecurity = "";
        private string _connectedIp = "";
        private string _connectedLinkSpeed = "";
        private string _adapterName = "";
        private bool _isPasswordPromptOpen = false;
        private string _passwordPromptSsid = "";
        private string _passwordPromptSecurity = "";
        private string _passwordPromptInput = "";
        private string _searchFilter = "";
        private bool _isTopmost = false;

        public bool IsMasterEnabled
        {
            get => _isMasterEnabled;
            set
            {
                _isMasterEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MasterToggleText));
                OnPropertyChanged(nameof(MasterToggleStatusColor));
                OnPropertyChanged(nameof(MasterButtonBackground));
                OnPropertyChanged(nameof(MasterButtonActionText));
            }
        }

        public string MasterToggleText => _isMasterEnabled ? "SECURE CONNECT: ON" : "SECURE CONNECT: OFF";
        public string MasterToggleStatusColor => _isMasterEnabled ? "#00E676" : "#7C88A8";
        public string MasterButtonBackground => _isMasterEnabled ? "#0D2E1F" : "#1A2030";
        public string MasterButtonActionText => _isMasterEnabled ? "Turn OFF" : "Turn ON";

        public bool IsFullPowerActive
        {
            get => _isFullPowerActive;
            set { _isFullPowerActive = value; OnPropertyChanged(); }
        }

        public bool IsScanning
        {
            get => _isScanning;
            set { _isScanning = value; OnPropertyChanged(); }
        }

        public bool IsConnecting
        {
            get => _isConnecting;
            set { _isConnecting = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string ConnectionState
        {
            get => _connectionState;
            set
            {
                _connectionState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsConnected));
            }
        }

        public bool IsConnected =>
            _connectionState.Equals("connected", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_connectedSsid) &&
            !_connectedSsid.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase);

        public string ConnectedSsid
        {
            get => _connectedSsid;
            set
            {
                _connectedSsid = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsConnected));
            }
        }

        public string ConnectedBssid
        {
            get => _connectedBssid;
            set { _connectedBssid = value; OnPropertyChanged(); }
        }

        public int ConnectedSignal
        {
            get => _connectedSignal;
            set { _connectedSignal = value; OnPropertyChanged(); }
        }

        public string ConnectedSecurity
        {
            get => _connectedSecurity;
            set { _connectedSecurity = value; OnPropertyChanged(); }
        }

        public string ConnectedIp
        {
            get => _connectedIp;
            set { _connectedIp = value; OnPropertyChanged(); }
        }

        public string ConnectedLinkSpeed
        {
            get => _connectedLinkSpeed;
            set { _connectedLinkSpeed = value; OnPropertyChanged(); }
        }

        public string AdapterName
        {
            get => _adapterName;
            set { _adapterName = value; OnPropertyChanged(); }
        }

        public bool IsPasswordPromptOpen
        {
            get => _isPasswordPromptOpen;
            set { _isPasswordPromptOpen = value; OnPropertyChanged(); }
        }

        public string PasswordPromptSsid
        {
            get => _passwordPromptSsid;
            set { _passwordPromptSsid = value; OnPropertyChanged(); }
        }

        public string PasswordPromptSecurity
        {
            get => _passwordPromptSecurity;
            set { _passwordPromptSecurity = value; OnPropertyChanged(); }
        }

        public string PasswordPromptInput
        {
            get => _passwordPromptInput;
            set { _passwordPromptInput = value; OnPropertyChanged(); }
        }

        public string SearchFilter
        {
            get => _searchFilter;
            set { _searchFilter = value; OnPropertyChanged(); }
        }

        public bool IsTopmost
        {
            get => _isTopmost;
            set
            {
                _isTopmost = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PinButtonText));
            }
        }

        public string PinButtonText => _isTopmost ? "Unpin" : "Pin";

        public ObservableCollection<WifiNetwork> AvailableNetworks { get; } = new();
        public ObservableCollection<SavedWifiProfile> SavedProfiles { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
