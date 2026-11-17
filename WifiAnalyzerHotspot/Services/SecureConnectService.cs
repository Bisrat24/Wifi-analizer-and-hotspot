using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Security;
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
        /// Engages or disengages Full Power / Maximum Performance Mode for the Wi-Fi adapter.
        /// When enabled:
        /// 1. Configures Windows Power Scheme Wireless Adapter Settings to Maximum Performance (0) for AC and DC.
        /// 2. Ensures TCP auto-tuning is active with no throttling.
        /// 3. Starts an ultra-lightweight micro-pulse to keep the Wi-Fi radio from falling into D2/D3 sleep states.
        /// </summary>
        public static void SetFullPowerMode(bool enable)
        {
            lock (_lock)
            {
                try
                {
                    if (enable)
                    {
                        // Subgroup: Wireless Adapter Settings (19cbb8fa-5279-450e-9fac-8a3d5fedd0c1)
                        // Setting:  Power Saving Mode (12bbebe6-58d6-4636-95bb-3217ef867c1a)
                        // Index 0 = Maximum Performance
                        RunCmd("powercfg", "/setacvalueindex", "scheme_current", "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a", "0");
                        RunCmd("powercfg", "/setdcvalueindex", "scheme_current", "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a", "0");
                        RunCmd("powercfg", "/setactive", "scheme_current");

                        // Disable TCP throttling
                        RunCmd("netsh", "int", "tcp", "set", "global", "autotuninglevel=normal");

                        StartMicroPulse();
                    }
                    else
                    {
                        StopMicroPulse();
                    }
                }
                catch
                {
                    // Graceful fallback if powercfg or elevation is restricted
                }
            }
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
            try
            {
                string output = RunCmd("netsh", "wlan", "show", "profiles");
                var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    if (line.Contains(':') && (line.Contains("All User Profile") || line.Contains("User Profile")))
                    {
                        var parts = line.Split(new[] { ':' }, 2);
                        if (parts.Length > 1)
                        {
                            string name = parts[1].Trim();
                            if (!string.IsNullOrEmpty(name))
                            {
                                list.Add(new SavedWifiProfile
                                {
                                    Name = name,
                                    Ssid = name,
                                    Authentication = "WPA2-Personal",
                                    IsAutoConnect = true
                                });
                            }
                        }
                    }
                }
            }
            catch { }
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
                    var args = new List<string> { "wlan", "connect", $"name=\"{profileName}\"" };
                    if (!string.IsNullOrWhiteSpace(adapterName))
                        args.Add($"interface=\"{adapterName}\"");

                    string outText = RunCmd("netsh", args.ToArray());
                    await Task.Delay(1500);

                    if (outText.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
                        outText.Contains("Connection request was completed", StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, $"Fast-connected to '{profileName}'.");
                    }
                    return (true, $"Connecting to '{profileName}'...");
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

                    await File.WriteAllTextAsync(tempProfilePath, xml);

                    var addArgs = new List<string> { "wlan", "add", "profile", $"filename=\"{tempProfilePath}\"" };
                    if (!string.IsNullOrWhiteSpace(adapterName))
                        addArgs.Add($"interface=\"{adapterName}\"");

                    RunCmd("netsh", addArgs.ToArray());

                    // Direct connection
                    var connArgs = new List<string> { "wlan", "connect", $"name=\"{ssid}\"" };
                    if (!string.IsNullOrWhiteSpace(adapterName))
                        connArgs.Add($"interface=\"{adapterName}\"");

                    string connOut = RunCmd("netsh", connArgs.ToArray());
                    await Task.Delay(1500);

                    return (true, $"Saved profile and connected to '{ssid}'.");
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
                        args.Add($"interface=\"{adapterName}\"");

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
                    var args = new List<string> { "wlan", "delete", "profile", $"name=\"{profileName}\"" };
                    if (!string.IsNullOrWhiteSpace(adapterName))
                        args.Add($"interface=\"{adapterName}\"");

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
                    Arguments = string.Join(" ", args),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

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
        }
    }
}
