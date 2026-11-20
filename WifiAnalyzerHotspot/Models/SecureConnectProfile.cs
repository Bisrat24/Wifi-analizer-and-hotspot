namespace WifiAnalyzerHotspot.Models
{
    /// <summary>
    /// A Wi-Fi profile owned by Secure Connect, independent of Windows saved networks.
    /// Passwords are DPAPI-protected on disk by SecureConnectProfileStore.
    /// </summary>
    public class SecureConnectProfile
    {
        public string Ssid { get; set; } = string.Empty;
        public string AuthType { get; set; } = "WPA2-Personal";
        public string Encryption { get; set; } = "AES";
        public bool IsHidden { get; set; }
        public string Password { get; set; } = string.Empty;
        public System.DateTime? LastConnected { get; set; }
    }
}
