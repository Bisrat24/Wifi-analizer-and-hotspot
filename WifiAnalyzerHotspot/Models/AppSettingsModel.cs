using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WifiAnalyzerHotspot.Models
{
    public class AppSettingsModel : INotifyPropertyChanged
    {
        // ==================== SECURE DNS (DoH) ====================
        private bool _isSecureDnsEnabled = false;
        private string _selectedDnsProvider = "Cloudflare (1.1.1.1)";
        private string _customDohUrl = "https://cloudflare-dns.com/dns-query";
        private string _dnsStatusMessage = "Secure DNS is disabled (using standard system DNS)";
        private bool _isDnsTesting = false;

        public bool IsSecureDnsEnabled
        {
            get => _isSecureDnsEnabled;
            set { _isSecureDnsEnabled = value; OnPropertyChanged(); }
        }

        public string SelectedDnsProvider
        {
            get => _selectedDnsProvider;
            set
            {
                _selectedDnsProvider = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomDohSelected));
                UpdateDohUrlFromPreset();
            }
        }

        public string CustomDohUrl
        {
            get => _customDohUrl;
            set { _customDohUrl = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public bool IsCustomDohSelected => _selectedDnsProvider.Contains("Custom", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public string DnsStatusMessage
        {
            get => _dnsStatusMessage;
            set { _dnsStatusMessage = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public bool IsDnsTesting
        {
            get => _isDnsTesting;
            set { _isDnsTesting = value; OnPropertyChanged(); }
        }

        private void UpdateDohUrlFromPreset()
        {
            if (_selectedDnsProvider.Contains("Cloudflare"))
                _customDohUrl = "https://cloudflare-dns.com/dns-query";
            else if (_selectedDnsProvider.Contains("Google"))
                _customDohUrl = "https://dns.google/dns-query";
            else if (_selectedDnsProvider.Contains("Quad9"))
                _customDohUrl = "https://dns.quad9.net/dns-query";
            else if (_selectedDnsProvider.Contains("AdGuard"))
                _customDohUrl = "https://dns.adguard-dns.com/dns-query";
            OnPropertyChanged(nameof(CustomDohUrl));
        }

        // ==================== CAPTIVE PORTAL & KEEP-ALIVE ====================
        private bool _useHttpHeadCaptivePortal = true;
        private string _selectedCaptivePortalPreset = "Microsoft Connect Test";
        private string _captivePortalUrl = "http://www.msftconnecttest.com/connecttest.txt";
        private bool _autoDetectCaptivePortalRedirect = true;
        private string _captivePortalStatusMessage = "Ready to test or keep alive";
        private bool _isCaptiveTesting = false;

        public bool UseHttpHeadCaptivePortal
        {
            get => _useHttpHeadCaptivePortal;
            set { _useHttpHeadCaptivePortal = value; OnPropertyChanged(); }
        }

        public string SelectedCaptivePortalPreset
        {
            get => _selectedCaptivePortalPreset;
            set
            {
                _selectedCaptivePortalPreset = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomCaptiveSelected));
                UpdateCaptiveUrlFromPreset();
            }
        }

        public string CaptivePortalUrl
        {
            get => _captivePortalUrl;
            set { _captivePortalUrl = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public bool IsCustomCaptiveSelected => _selectedCaptivePortalPreset.Contains("Custom", StringComparison.OrdinalIgnoreCase);

        public bool AutoDetectCaptivePortalRedirect
        {
            get => _autoDetectCaptivePortalRedirect;
            set { _autoDetectCaptivePortalRedirect = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public string CaptivePortalStatusMessage
        {
            get => _captivePortalStatusMessage;
            set { _captivePortalStatusMessage = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public bool IsCaptiveTesting
        {
            get => _isCaptiveTesting;
            set { _isCaptiveTesting = value; OnPropertyChanged(); }
        }

        private void UpdateCaptiveUrlFromPreset()
        {
            if (_selectedCaptivePortalPreset.Contains("Microsoft"))
                _captivePortalUrl = "http://www.msftconnecttest.com/connecttest.txt";
            else if (_selectedCaptivePortalPreset.Contains("Google"))
                _captivePortalUrl = "http://connectivitycheck.gstatic.com/generate_204";
            else if (_selectedCaptivePortalPreset.Contains("Mozilla") || _selectedCaptivePortalPreset.Contains("Firefox"))
                _captivePortalUrl = "http://detectportal.firefox.com/canonical.html";
            else if (_selectedCaptivePortalPreset.Contains("Cloudflare"))
                _captivePortalUrl = "http://cp.cloudflare.com/generate_204";
            OnPropertyChanged(nameof(CaptivePortalUrl));
        }

        // ==================== PROXY SETTINGS ====================
        private bool _isProxyEnabled = false;
        private string _proxyType = "System Proxy"; // "System Proxy" or "Manual Proxy"
        private string _proxyHost = "127.0.0.1";
        private int _proxyPort = 8080;
        private bool _proxyBypassLocal = true;
        private string _proxyUsername = "";
        private string _proxyPassword = "";
        private string _proxyStatusMessage = "Proxy is disabled (Direct Connection)";
        private bool _isProxyTesting = false;

        public bool IsProxyEnabled
        {
            get => _isProxyEnabled;
            set { _isProxyEnabled = value; OnPropertyChanged(); }
        }

        public string ProxyType
        {
            get => _proxyType;
            set
            {
                _proxyType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsManualProxySelected));
            }
        }

        [JsonIgnore]
        public bool IsManualProxySelected => _proxyType.Contains("Manual", StringComparison.OrdinalIgnoreCase);

        public string ProxyHost
        {
            get => _proxyHost;
            set { _proxyHost = value; OnPropertyChanged(); }
        }

        public int ProxyPort
        {
            get => _proxyPort;
            set { _proxyPort = Math.Clamp(value, 1, 65535); OnPropertyChanged(); }
        }

        public bool ProxyBypassLocal
        {
            get => _proxyBypassLocal;
            set { _proxyBypassLocal = value; OnPropertyChanged(); }
        }

        public string ProxyUsername
        {
            get => _proxyUsername;
            set { _proxyUsername = value; OnPropertyChanged(); }
        }

        public string ProxyPassword
        {
            get => _proxyPassword;
            set { _proxyPassword = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public string ProxyStatusMessage
        {
            get => _proxyStatusMessage;
            set { _proxyStatusMessage = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public bool IsProxyTesting
        {
            get => _isProxyTesting;
            set { _isProxyTesting = value; OnPropertyChanged(); }
        }

        // ==================== ADVANCED NETWORK CONDITIONS ====================
        private int _probeTimeoutMs = 1500;
        private int _vpnReconnectBufferSeconds = 3;
        private bool _dnsFallbackEnabled = true;

        public int ProbeTimeoutMs
        {
            get => _probeTimeoutMs;
            set { _probeTimeoutMs = Math.Clamp(value, 300, 10000); OnPropertyChanged(); }
        }

        public int VpnReconnectBufferSeconds
        {
            get => _vpnReconnectBufferSeconds;
            set { _vpnReconnectBufferSeconds = Math.Clamp(value, 1, 30); OnPropertyChanged(); }
        }

        public bool DnsFallbackEnabled
        {
            get => _dnsFallbackEnabled;
            set { _dnsFallbackEnabled = value; OnPropertyChanged(); }
        }

        // ==================== WINDOWS STARTUP & AUTOMATION ====================
        private bool _startWithWindows = false;
        private bool _startMinimized = false;
        private bool _autoStartHotspotOnLaunch = false;
        private bool _autoStartStabilizerOnLaunch = false;
        private string _startupStatusMessage = "Startup configuration ready";
        private string _analyzerViewMode = "Spectrum"; // "Spectrum" or "Timeline"

        public bool StartWithWindows
        {
            get => _startWithWindows;
            set { _startWithWindows = value; OnPropertyChanged(); }
        }

        public bool StartMinimized
        {
            get => _startMinimized;
            set { _startMinimized = value; OnPropertyChanged(); }
        }

        public bool AutoStartHotspotOnLaunch
        {
            get => _autoStartHotspotOnLaunch;
            set { _autoStartHotspotOnLaunch = value; OnPropertyChanged(); }
        }

        public bool AutoStartStabilizerOnLaunch
        {
            get => _autoStartStabilizerOnLaunch;
            set { _autoStartStabilizerOnLaunch = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public string StartupStatusMessage
        {
            get => _startupStatusMessage;
            set { _startupStatusMessage = value; OnPropertyChanged(); }
        }

        public string AnalyzerViewMode
        {
            get => _analyzerViewMode;
            set { _analyzerViewMode = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? prop = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
