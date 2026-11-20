using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class SecureConnectService : IDisposable
    {
        private static CancellationTokenSource? _pulseCts;
        private static readonly object _lock = new();

        /// <summary>
        /// Engages or disengages Secure Connect power features. Only per-adapter Wi-Fi
        /// power-saving properties are changed, and every change is staged for restore.
        /// </summary>
        public static void SetFullPowerMode(bool enable)
        {
            lock (_lock)
            {
                try
                {
                    if (enable)
                    {
                        if (IsWifiConnected()) ApplySafeAdapterPowerMode();
                        StartMicroPulse();
                    }
                    else
                    {
                        StopMicroPulse();
                        SystemRestoreService.Release("SecureConnectPower");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SecureConnect] power mode error: {ex.Message}");
                }
            }
        }

        private static bool IsWifiConnected()
        {
            try
            {
                string ssid = new WifiScannerService().GetConnectedInterfaceInfo().Ssid ?? string.Empty;
                return !string.IsNullOrWhiteSpace(ssid) &&
                       !ssid.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private const string QueryWifiPowerScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$wifi = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and ($_.InterfaceDescription -match 'Wireless|Wi-Fi|802\.11') }
foreach ($a in $wifi) {
    $props = Get-NetAdapterAdvancedProperty -Name $a.Name -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'Power Saving|Power Save' }
    foreach ($p in $props) {
        Write-Output ('PWR_ORIGINAL:' + $a.Name + '|' + $p.DisplayName + '|' + $p.DisplayValue)
    }
}
";

        private const string ApplyWifiPowerScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$wifi = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and ($_.InterfaceDescription -match 'Wireless|Wi-Fi|802\.11') }
foreach ($a in $wifi) {
    $props = Get-NetAdapterAdvancedProperty -Name $a.Name -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'Power Saving|Power Save' }
    foreach ($p in $props) {
        $enableVal = @($p.ValidDisplayValues) | Where-Object { $_ -match 'Off|Disable|Maximum Performance|Best' } | Select-Object -First 1
        if ($enableVal) {
            try { Set-NetAdapterAdvancedProperty -Name $a.Name -DisplayName $p.DisplayName -DisplayValue $enableVal -ErrorAction Stop | Out-Null } catch {}
        }
    }
}
";

        private static void ApplySafeAdapterPowerMode()
        {
            var query = ProcessRunner.RunPowerShellAsync(QueryWifiPowerScript, 10000).GetAwaiter().GetResult();
            if (!query.Completed || string.IsNullOrWhiteSpace(query.Output)) return;

            var restore = new List<string>();
            foreach (string raw in query.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (!line.StartsWith("PWR_ORIGINAL:", StringComparison.Ordinal)) continue;
                string[] parts = line.Substring("PWR_ORIGINAL:".Length).Split('|');
                if (parts.Length != 3) continue;
                restore.Add($"try {{ Set-NetAdapterAdvancedProperty -Name '{SystemRestoreService.Escape(parts[0])}' -DisplayName '{SystemRestoreService.Escape(parts[1])}' -DisplayValue '{SystemRestoreService.Escape(parts[2])}' -ErrorAction SilentlyContinue }} catch {{}}");
            }
            if (restore.Count == 0) return;

            SystemRestoreService.Stage("SecureConnectPower", string.Join(Environment.NewLine, restore));
            ProcessRunner.RunPowerShellAsync(ApplyWifiPowerScript, 10000).GetAwaiter().GetResult();
        }

        private static void StartMicroPulse()
        {
            StopMicroPulse();
            _pulseCts = new CancellationTokenSource();
            var token = _pulseCts.Token;

            Task.Run(async () =>
            {
                using var ping = new Ping();
                var buffer = new byte[16];
                var options = new PingOptions(64, true);

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Ultra-lightweight keep-alive pulse keeps the 802.11 radio wake timer active
                        await ping.SendPingAsync("1.1.1.1", 400, buffer, options);
                    }
                    catch { }

                    try
                    {
                        await Task.Delay(8000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }

        private static void StopMicroPulse()
        {
            try
            {
                _pulseCts?.Cancel();
                _pulseCts?.Dispose();
                _pulseCts = null;
            }
            catch { }
        }

        /// <summary>
        /// Builds the WLAN profile XML used for a temporary, non-persisted connection.
        /// </summary>
        internal static string BuildProfileXml(string ssid, string password, string authType, string encType)
        {
            string result = BuildProfileXmlCore(ssid, password, authType, encType);
            NativeWlanService.LogProfileXml(result);
            return result;
        }

        private static string BuildProfileXmlCore(string ssid, string password, string authType, string encType)
        {
            bool requestedOpen = authType.Equals("Open", StringComparison.OrdinalIgnoreCase);
            bool isOpen = requestedOpen || string.IsNullOrWhiteSpace(password);
            if (!requestedOpen && string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("A security key is required for this network.");

            string xmlAuth = "WPA2PSK", xmlEnc = "AES";
            if (authType.Contains("WPA3", StringComparison.OrdinalIgnoreCase))
            {
                xmlAuth = "WPA3SAE";
            }
            else if (authType.Contains("WPA2", StringComparison.OrdinalIgnoreCase))
            {
                xmlEnc = encType.Contains("TKIP", StringComparison.OrdinalIgnoreCase) ? "TKIP" : "AES";
            }
            else if (isOpen)
            {
                xmlAuth = "open";
                xmlEnc = "none";
            }

            string name = SecurityElement.Escape(ssid);
            string transition = xmlAuth == "WPA3SAE"
                ? "<transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>"
                : string.Empty;
            if (isOpen)
            {
                return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""https://www.microsoft.com/networking/WLAN/profile/v1"">
    <name>{name}</name>
    <SSIDConfig><SSID><name>{name}</name></SSID></SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>manual</connectionMode>
    <autoSwitch>false</autoSwitch>
    <MSM><security><authEncryption>
        <authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX>
    </authEncryption></security></MSM>
</WLANProfile>";
            }

            return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""https://www.microsoft.com/networking/WLAN/profile/v1"">
    <name>{name}</name>
    <SSIDConfig><SSID><name>{name}</name></SSID></SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>manual</connectionMode>
    <autoSwitch>false</autoSwitch>
    <MSM><security>
        <authEncryption>
            <authentication>{xmlAuth}</authentication><encryption>{xmlEnc}</encryption><useOneX>false</useOneX>{transition}
        </authEncryption>
        <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey>
    </security></MSM>
</WLANProfile>";
        }

        /// <summary>
        /// Copies existing Windows saved networks into the app store once, without modifying Windows.
        /// </summary>
        private static void ImportWindowsProfilesOnce()
        {
            if (SecureConnectProfileStore.IsImported) return;
            try
            {
                foreach (string name in NativeWlanService.GetProfileNames())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (SecureConnectProfileStore.Find(name) != null) continue;

                    var result = ProcessRunner.RunAsync("netsh.exe",
                        new[] { "wlan", "show", "profile", $"name={name}", "key=clear" },
                        timeoutMs: 5000).GetAwaiter().GetResult();
                    if (!result.Completed || result.ExitCode != 0) continue;

                    string auth = "WPA2-Personal";
                    string cipher = "AES";
                    string password = string.Empty;

                    var authMatch = Regex.Match(result.Output, @"Authentication\s*:\s*(.+)", RegexOptions.IgnoreCase);
                    if (authMatch.Success) auth = authMatch.Groups[1].Value.Trim();
                    var cipherMatch = Regex.Match(result.Output, @"Cipher\s*:\s*(.+)", RegexOptions.IgnoreCase);
                    if (cipherMatch.Success) cipher = cipherMatch.Groups[1].Value.Trim();
                    var keyMatch = Regex.Match(result.Output, @"Key Content\s*:\s*(.+)", RegexOptions.IgnoreCase);
                    if (keyMatch.Success) password = keyMatch.Groups[1].Value.Trim();

                    SecureConnectProfileStore.Upsert(new Models.SecureConnectProfile
                    {
                        Ssid = name,
                        AuthType = auth,
                        Encryption = cipher,
                        Password = password
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[SecureConnect] import failed: {ex.Message}"); }
            finally { SecureConnectProfileStore.MarkImported(); }
        }

        /// <summary>
        /// Reads profiles owned by Secure Connect. Windows saved networks are never touched.
        /// </summary>
        public static List<SavedWifiProfile> GetSavedProfiles()
        {
            ImportWindowsProfilesOnce();
            return SecureConnectProfileStore.Load().Select(p => new SavedWifiProfile
            {
                Name = p.Ssid,
                Ssid = p.Ssid,
                Authentication = p.AuthType,
                IsAutoConnect = true
            }).ToList();
        }

        /// <summary>
        /// Retrieves the cleartext security key/passphrase for a saved Wi-Fi profile using native netsh export.
        /// Returns null if the network is open, absent, or cannot be queried.
        /// </summary>
        public static Task<string?> GetProfilePasswordAsync(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return Task.FromResult<string?>(null);
            var profile = SecureConnectProfileStore.Find(profileName);
            return Task.FromResult(profile?.Password);
        }

        /// <summary>
        /// Fast connects to a previously saved Wi-Fi profile in under 1 second.
        /// </summary>
        public static async Task<(bool success, string message)> ConnectToProfileAsync(string profileName, string? adapterName = null)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return (false, "Invalid profile name.");

            var profile = SecureConnectProfileStore.Find(profileName);
            if (profile == null) return (false, $"'{profileName}' is not a Secure Connect profile.");

            try
            {
                string xml = BuildProfileXml(profile.Ssid, profile.Password, profile.AuthType, profile.Encryption);
                var result = await NativeWlanService.ConnectWithTemporaryProfileAsync(xml, profile.Ssid, adapterName);
                if (result.success)
                {
                    profile.LastConnected = DateTime.Now;
                    SecureConnectProfileStore.Upsert(profile);
                }
                return result;
            }
            catch (Exception ex) { return (false, $"Connection failed: {ex.Message}"); }
        }

        /// <summary>
        /// Connects to any Wi-Fi network (new or saved) with password credentials.
        /// Automatically generates a WPA2/WPA3 profile XML, registers it, and initiates full-speed connection.
        /// </summary>
        public static async Task<(bool success, string message)> ConnectWithPasswordAsync(
            string ssid,
            string password,
            string authType,
            string encType,
            string? adapterName = null)
        {
            if (string.IsNullOrWhiteSpace(ssid))
                return (false, "SSID cannot be empty.");

            try
            {
                string xml = BuildProfileXml(ssid, password, authType, encType);
                var result = await NativeWlanService.ConnectWithTemporaryProfileAsync(xml, ssid, adapterName);
                if (!result.success) return result;

                SecureConnectProfileStore.Upsert(new Models.SecureConnectProfile
                {
                    Ssid = ssid,
                    AuthType = authType,
                    Encryption = encType,
                    Password = password,
                    IsHidden = false,
                    LastConnected = DateTime.Now
                });
                return (true, $"Connected to '{ssid}' and saved it to Secure Connect.");
            }
            catch (Exception ex) { return (false, $"Failed to connect to '{ssid}': {ex.Message}"); }
        }

        /// <summary>
        /// Disconnects the active Wi-Fi connection.
        /// </summary>
        public static Task<bool> DisconnectAsync(string? adapterName = null)
        {
            return Task.Run(async () => (await NativeWlanService.DisconnectAsync(adapterName)).success);
        }

        /// <summary>
        /// Forgets a profile from the Secure Connect store only. Windows saved networks are untouched.
        /// </summary>
        public static Task<bool> ForgetProfileAsync(string profileName, string? adapterName = null)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return Task.FromResult(false);
            SecureConnectProfileStore.Remove(profileName);
            return Task.FromResult(true);
        }

        private static string RunCmd(string exe, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // ArgumentList quotes each value itself, so SSIDs with spaces or quotes
                // cannot break out into extra netsh arguments.
                foreach (string arg in args) psi.ArgumentList.Add(arg);

                using var proc = Process.Start(psi);
                if (proc == null) return string.Empty;

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(3000);

                return stdout + " " + stderr;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            StopMicroPulse();
            SystemRestoreService.Release("SecureConnectPower");
        }
    }
}
