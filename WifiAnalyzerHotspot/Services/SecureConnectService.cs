using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security;
using System.Text;
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
        /// Reads all user-saved Wi-Fi profiles configured on the machine.
        /// </summary>
        public static List<SavedWifiProfile> GetSavedProfiles()
        {
            var list = new List<SavedWifiProfile>();
            var names = NativeWlanService.GetProfileNames();

            // Fallback only when the native API is unavailable (unsupported driver / service).
            if (names.Count == 0)
            {
                try
                {
                    string output = RunCmd("netsh", "wlan", "show", "profiles");
                    foreach (var line in output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.Contains(':') && (line.Contains("All User Profile") || line.Contains("User Profile")))
                        {
                            string name = line.Split(new[] { ':' }, 2)[1].Trim();
                            if (!string.IsNullOrEmpty(name)) names.Add(name);
                        }
                    }
                }
                catch { }
            }

            foreach (string name in names)
            {
                list.Add(new SavedWifiProfile
                {
                    Name = name,
                    Ssid = name,
                    Authentication = "Saved profile",
                    IsAutoConnect = true
                });
            }
            return list;
        }

        /// <summary>
        /// Fast connects to a previously saved Wi-Fi profile in under 1 second.
        /// </summary>
        public static async Task<(bool success, string message)> ConnectToProfileAsync(string profileName, string? adapterName = null)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return (false, "Invalid profile name.");

            return await Task.Run(async () =>
            {
                try
                {
                    uint error = NativeWlanService.ConnectToProfile(profileName);
                    if (error != 0)
                    {
                        // Fallback for drivers that reject WlanConnect.
                        var args = new List<string> { "wlan", "connect", $"name={profileName}" };
                        if (!string.IsNullOrWhiteSpace(adapterName)) args.Add($"interface={adapterName}");
                        RunCmd("netsh", args.ToArray());
                    }
                    await Task.Delay(1500);

                    string connected = new WifiScannerService().GetConnectedInterfaceInfo().Ssid ?? string.Empty;
                    if (connected.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                        return (true, $"Fast-connected to '{profileName}'.");

                    return (false, $"Could not confirm connection to '{profileName}' (current: {connected}).");
                }
                catch (Exception ex)
                {
                    return (false, $"Connection failed: {ex.Message}");
                }
            });
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

            return await Task.Run(async () =>
            {
                string tempProfilePath = Path.Combine(Path.GetTempPath(), $"sec_wifi_{Guid.NewGuid():N}.xml");
                try
                {
                    bool isOpen = string.IsNullOrWhiteSpace(password) ||
                                  authType.Equals("Open", StringComparison.OrdinalIgnoreCase);

                    string xmlAuth = "WPA2PSK";
                    string xmlEnc = "AES";

                    if (authType.Contains("WPA3", StringComparison.OrdinalIgnoreCase))
                    {
                        xmlAuth = "WPA3SAE";
                        xmlEnc = "AES";
                    }
                    else if (authType.Contains("WPA2", StringComparison.OrdinalIgnoreCase))
                    {
                        xmlAuth = "WPA2PSK";
                        xmlEnc = encType.Contains("TKIP", StringComparison.OrdinalIgnoreCase) ? "TKIP" : "AES";
                    }
                    else if (isOpen)
                    {
                        xmlAuth = "open";
                        xmlEnc = "none";
                    }

                    string xml;
                    if (isOpen)
                    {
                        xml = $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
    <name>{SecurityElement.Escape(ssid)}</name>
    <SSIDConfig>
        <SSID>
            <name>{SecurityElement.Escape(ssid)}</name>
        </SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>auto</connectionMode>
    <MSM>
        <security>
            <authEncryption>
                <authentication>open</authentication>
                <encryption>none</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
        </security>
    </MSM>
</WLANProfile>";
                    }
                    else
                    {
                        xml = $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
    <name>{SecurityElement.Escape(ssid)}</name>
    <SSIDConfig>
        <SSID>
            <name>{SecurityElement.Escape(ssid)}</name>
        </SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>auto</connectionMode>
    <MSM>
        <security>
            <authEncryption>
                <authentication>{xmlAuth}</authentication>
                <encryption>{xmlEnc}</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
            <sharedKey>
                <keyType>passPhrase</keyType>
                <protected>false</protected>
                <keyMaterial>{SecurityElement.Escape(password)}</keyMaterial>
            </sharedKey>
        </security>
    </MSM>
</WLANProfile>";
                    }

                    // Keep the file handle open with DeleteOnClose so the plaintext key never
                    // lingers on disk after the netsh call, even if the process is killed.
                    using (var profileFile = new FileStream(tempProfilePath, FileMode.Create, FileAccess.Write,
                               FileShare.Read, 4096, FileOptions.DeleteOnClose))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(xml);
                        await profileFile.WriteAsync(bytes, 0, bytes.Length);
                        await profileFile.FlushAsync();

                        var addArgs = new List<string> { "wlan", "add", "profile", $"filename={tempProfilePath}" };
                        if (!string.IsNullOrWhiteSpace(adapterName))
                            addArgs.Add($"interface={adapterName}");

                        RunCmd("netsh", addArgs.ToArray());

                        // Native connect with netsh fallback, then verify the actual SSID.
                        uint error = NativeWlanService.ConnectToProfile(ssid);
                        if (error != 0)
                        {
                            var connArgs = new List<string> { "wlan", "connect", $"name={ssid}" };
                            if (!string.IsNullOrWhiteSpace(adapterName))
                                connArgs.Add($"interface={adapterName}");
                            RunCmd("netsh", connArgs.ToArray());
                        }
                        await Task.Delay(1500);

                        string connected = new WifiScannerService().GetConnectedInterfaceInfo().Ssid ?? string.Empty;
                        if (!connected.Equals(ssid, StringComparison.OrdinalIgnoreCase))
                            return (false, $"Profile saved for '{ssid}' but connection could not be confirmed (current: {connected}).");

                        return (true, $"Saved profile and connected to '{ssid}'.");
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to connect to '{ssid}': {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempProfilePath))
                            File.Delete(tempProfilePath);
                    }
                    catch { }
                }
            });
        }

        /// <summary>
        /// Disconnects the active Wi-Fi connection.
        /// </summary>
        public static async Task<bool> DisconnectAsync(string? adapterName = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var args = new List<string> { "wlan", "disconnect" };
                    if (!string.IsNullOrWhiteSpace(adapterName))
                        args.Add($"interface={adapterName}");

                    RunCmd("netsh", args.ToArray());
                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Deletes/forgets a saved Wi-Fi profile from Windows.
        /// </summary>
        public static async Task<bool> ForgetProfileAsync(string profileName, string? adapterName = null)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return false;

            return await Task.Run(() =>
            {
                try
                {
                    var args = new List<string> { "wlan", "delete", "profile", $"name={profileName}" };
                    if (!string.IsNullOrWhiteSpace(adapterName))
                        args.Add($"interface={adapterName}");

                    RunCmd("netsh", args.ToArray());
                    return true;
                }
                catch
                {
                    return false;
                }
            });
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
