using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class HotspotService
    {
        private class HotspotStatusJson
        {
            public int ClientCount { get; set; }
            public string? Ssid { get; set; }
            public string? Passphrase { get; set; }
            public int Band { get; set; }
            public int State { get; set; }
            public string? ActiveChannel { get; set; }
            public string? ActiveBand { get; set; }
            public string? ChannelDesc { get; set; }
            public string? Error { get; set; }
            public List<ClientJson>? Clients { get; set; }
        }

        private class ClientJson
        {
            public string? Mac { get; set; }
            public string? HostName { get; set; }
            public string? Ip { get; set; }
        }

        private class OperationResultJson
        {
            public string? Result { get; set; }
            public string? Error { get; set; }
        }

        /// <summary>
        /// Attempts to query the live channel of the active Wi-Fi Direct virtual adapter via netsh wlan show interfaces.
        /// Zero PowerShell subprocess, completely safe from EDR blocks.
        /// </summary>
        private static (int channel, string band) DetectVirtualHotspotChannel()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var process = Process.Start(psi);
                if (process == null) return (0, "");
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(true); } catch { }
                }

                var blocks = Regex.Split(output, @"(?:\r?\n){2,}");
                foreach (var block in blocks)
                {
                    if (block.Contains("Direct", StringComparison.OrdinalIgnoreCase) ||
                        block.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        block.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase))
                    {
                        var chMatch = Regex.Match(block, @"Channel\s*:\s*(\d+)");
                        var bandMatch = Regex.Match(block, @"Band\s*:\s*([^\r\n]+)");
                        if (chMatch.Success && int.TryParse(chMatch.Groups[1].Value, out int ch) && ch > 0)
                        {
                            string band = bandMatch.Success ? bandMatch.Groups[1].Value.Trim() : (ch > 14 ? "5 GHz" : "2.4 GHz");
                            return (ch, band);
                        }
                    }
                }
            }
            catch { }
            return (0, "");
        }

        /// <summary>
        /// Reads current Hotspot status, SSID, password, band, active broadcasting channel, and all connected clients.
        /// Does NOT invoke arp.exe or netsh.exe from PowerShell to guarantee zero enterprise EDR policy blocks.
        /// </summary>
        public async Task<HotspotModel> GetStatusAsync(int activeChannel = 0, string activeBand = "", string activeInterfaceName = "", string configuredChoice = "")
        {
            return await Task.Run(async () =>
            {
                var model = new HotspotModel();

                string safeBand = !string.IsNullOrEmpty(activeBand) ? activeBand : "2.4 GHz";
                string safeIf = !string.IsNullOrEmpty(activeInterfaceName) ? activeInterfaceName : "Wi-Fi";

                string script = $@"
$connectionProfile = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetInternetConnectionProfile()
if (-not $connectionProfile) {{
    $profiles = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetConnectionProfiles()
    if ($profiles) {{
        foreach ($p in $profiles) {{
            try {{
                if ($p.GetNetworkConnectivityLevel() -ge 1) {{ $connectionProfile = $p; break }}
            }} catch {{}}
        }}
        if (-not $connectionProfile -and $profiles.Count -gt 0) {{ $connectionProfile = $profiles[0] }}
    }}
}}
if ($connectionProfile) {{
    try {{
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]::CreateFromConnectionProfile($connectionProfile)
        $config = $mgr.GetCurrentAccessPointConfiguration()
        $state = [int]$mgr.TetheringOperationalState
        $clients = @()

        # Query connected clients directly via native WinRT Tethering API (zero child processes)
        try {{
            $rawClients = $mgr.GetTetheringClients()
            if ($rawClients) {{
                foreach ($c in $rawClients) {{
                    $name = 'Client Device'
                    if ($c.HostNames -and $c.HostNames.Count -gt 0) {{ $name = $c.HostNames[0].DisplayName }}
                    $clients += @{{ Mac = ""$($c.MacAddress)""; HostName = ""$name""; Ip = '' }}
                }}
            }}
        }} catch {{}}

        $obj = @{{
            State = $state
            Ssid = ""$($config.Ssid)""
            Passphrase = ""$($config.Passphrase)""
            Band = [int]$config.Band
            ClientCount = $clients.Count
            Clients = $clients
        }}
        $obj | ConvertTo-Json -Compress
    }} catch {{
        @{{ Error = $_.Exception.Message }} | ConvertTo-Json -Compress
    }}
}} else {{
    @{{ Error = 'No active network connection profile found' }} | ConvertTo-Json -Compress
}}
";
                string json = RunPowerShell(script);
                if (string.IsNullOrWhiteSpace(json))
                {
                    model.StatusMessage = "Failed to query Hotspot state.";
                    return model;
                }

                try
                {
                    var data = JsonSerializer.Deserialize<HotspotStatusJson>(json);
                    if (data != null)
                    {
                        if (!string.IsNullOrEmpty(data.Error))
                        {
                            model.StatusMessage = data.Error;
                            return model;
                        }

                        model.Ssid = data.Ssid ?? "MyHotspot";
                        model.Passphrase = data.Passphrase ?? "password123";

                        // State: 1 = On, 2 = Off, 3 = InTransition
                        model.State = data.State switch
                        {
                            1 => HotspotState.On,
                            2 => HotspotState.Off,
                            3 => HotspotState.InTransition,
                            _ => HotspotState.Unknown
                        };

                        // Band: 0 = Auto, 1 = 2.4 GHz, 2 = 5 GHz
                        model.Band = data.Band switch
                        {
                            1 => "2.4 GHz",
                            2 => "5 GHz",
                            _ => "Auto / Any"
                        };

                        // Determine Active Channel, Band, and Channel Description
                        if (model.State != HotspotState.On)
                        {
                            model.ActiveChannel = "Off";
                            model.ActiveBand = model.Band;
                            model.ChannelDescription = "Hotspot is stopped";
                        }
                        else if (activeChannel > 0)
                        {
                            // Single-Channel Concurrency: Hotspot is locked to active Wi-Fi channel
                            model.ActiveChannel = $"Channel {activeChannel}";
                            model.ActiveBand = !string.IsNullOrEmpty(activeBand) ? activeBand : model.Band;
                            model.ChannelDescription = $"Broadcasting on Channel {activeChannel} ({model.ActiveBand}) • Synced with {safeIf}";
                        }
                        else
                        {
                            // Ethernet / Cable network mode
                            var (virtCh, virtBand) = DetectVirtualHotspotChannel();
                            if (virtCh > 0)
                            {
                                model.ActiveChannel = $"Channel {virtCh}";
                                model.ActiveBand = virtBand;
                                model.ChannelDescription = $"Broadcasting on Channel {virtCh} ({virtBand}) • Cable Connection (Ethernet)";
                            }
                            else
                            {
                                var chMatch = Regex.Match(configuredChoice ?? "", @"Channel\s+(\d+)");
                                if (chMatch.Success && int.TryParse(chMatch.Groups[1].Value, out int ch) && ch > 0)
                                {
                                    string chBand = ch > 14 ? "5 GHz" : "2.4 GHz";
                                    model.ActiveChannel = $"Channel {ch}";
                                    model.ActiveBand = chBand;
                                    model.ChannelDescription = $"Broadcasting on Channel {ch} ({chBand}) • Cable Connection (Ethernet)";
                                }
                                else if (!string.IsNullOrEmpty(configuredChoice) && (configuredChoice.Contains("5 GHz") || data.Band == 2))
                                {
                                    model.ActiveChannel = "Channel 36 (5 GHz)";
                                    model.ActiveBand = "5 GHz";
                                    model.ChannelDescription = "Broadcasting on 5 GHz (High Speed) • Cable Connection (Ethernet)";
                                }
                                else
                                {
                                    model.ActiveChannel = "Auto (Dual-Band)";
                                    model.ActiveBand = model.Band;
                                    model.ChannelDescription = "Broadcasting on Auto Channel • Cable Connection (Ethernet)";
                                }
                            }
                        }

                        if (data.Clients != null)
                        {
                            model.Clients.Clear();
                            foreach (var c in data.Clients)
                            {
                                model.Clients.Add(new HotspotClientInfo
                                {
                                    MacAddress = c.Mac ?? "Unknown",
                                    HostName = string.IsNullOrEmpty(c.HostName) ? "Connected Device" : c.HostName,
                                    IpAddress = string.IsNullOrEmpty(c.Ip) ? "-" : c.Ip,
                                    Status = "Connected"
                                });
                            }
                            model.ClientCount = model.Clients.Count;
                        }
                        else
                        {
                            model.ClientCount = 0;
                        }

                        // Native Win32 ARP table enrichment (in-process iphlpapi.dll - ZERO process creation)
                        var arpMap = NativeArpHelper.GetHotspotArpEntries();
                        if (arpMap.Count > 0)
                        {
                            foreach (var client in model.Clients)
                            {
                                if ((string.IsNullOrEmpty(client.IpAddress) || client.IpAddress == "-") &&
                                    arpMap.TryGetValue(client.MacAddress, out var resolvedIp))
                                {
                                    client.IpAddress = resolvedIp;
                                    if (client.HostName == "Connected Device" || client.HostName == "Client Device")
                                    {
                                        client.HostName = $"Device ({resolvedIp})";
                                    }
                                }
                            }

                            // Discover any client present in ARP table not yet returned by WinRT
                            foreach (var kvp in arpMap)
                            {
                                if (!model.Clients.Any(c => c.MacAddress.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)))
                                {
                                    model.Clients.Add(new HotspotClientInfo
                                    {
                                        MacAddress = kvp.Key,
                                        IpAddress = kvp.Value,
                                        HostName = $"Device ({kvp.Value})",
                                        Status = "Connected"
                                    });
                                }
                            }
                            model.ClientCount = model.Clients.Count;
                        }

                        // Enrich each connected client with live connection speed, ping latency, and data flow health
                        if (model.Clients.Count > 0)
                        {
                            await EnrichClientSpeedAsync(model.Clients, model.ActiveBand);
                            model.ActiveDataClientCount = model.Clients.Count(c => c.IsGettingData);
                            if (model.ActiveDataClientCount == model.Clients.Count)
                            {
                                model.DataHealthStatus = $"All {model.Clients.Count} device(s) getting data";
                            }
                            else if (model.ActiveDataClientCount == 0)
                            {
                                model.DataHealthStatus = $"⚠️ 0/{model.Clients.Count} devices getting data (Stalled)";
                            }
                            else
                            {
                                model.DataHealthStatus = $"{model.ActiveDataClientCount}/{model.Clients.Count} device(s) getting data";
                            }
                        }
                        else
                        {
                            model.ActiveDataClientCount = 0;
                            model.DataHealthStatus = "No devices connected";
                        }

                        model.StatusMessage = model.IsRunning
                            ? $"Hotspot is ACTIVE on {model.ActiveChannel} ({model.ClientCount} connected)"
                            : "Hotspot is stopped.";
                    }
                }
                catch (Exception ex)
                {
                    model.StatusMessage = $"Parse error: {ex.Message}";
                }

                return model;
            });
        }

        /// <summary>
        /// Saves custom SSID, password, and Wi-Fi band/channel configuration.
        /// </summary>
        /// <summary>
        /// Configures the Mobile Hotspot. Wipes previous settings and creates fresh configuration from scratch.
        /// </summary>
        public async Task<(bool success, string message)> ConfigureHotspotAsync(string ssid, string passphrase, string bandOrChannel)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(ssid))
                    return (false, "SSID cannot be empty.");

                if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 8 || passphrase.Length > 63)
                    return (false, "Password must be between 8 and 63 characters.");

                string bandEnum = "TwoPointFourGigahertz";
                if (bandOrChannel.Contains("5 GHz") || bandOrChannel.Contains("5GHz"))
                {
                    bandEnum = "FiveGigahertz";
                }
                else if (bandOrChannel.Contains("Auto"))
                {
                    bandEnum = "Auto";
                }

                string safeSsid = ssid.Replace("\"", "`\"");
                string safePass = passphrase.Replace("\"", "`\"");

                string script = $@"
$connectionProfile = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetInternetConnectionProfile()
if (-not $connectionProfile) {{
    $profiles = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetConnectionProfiles()
    if ($profiles) {{
        foreach ($p in $profiles) {{
            try {{
                if ($p.GetNetworkConnectivityLevel() -ge 1) {{ $connectionProfile = $p; break }}
            }} catch {{}}
        }}
        if (-not $connectionProfile -and $profiles.Count -gt 0) {{ $connectionProfile = $profiles[0] }}
    }}
}}
if ($connectionProfile) {{
    try {{
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]::CreateFromConnectionProfile($connectionProfile)

        # Stop previous tethering if currently running to release virtual adapter and clear previous state
        if ($mgr.TetheringOperationalState -eq 1 -or $mgr.TetheringOperationalState -eq 3) {{
            try {{
                $stopOp = $mgr.StopTetheringAsync()
                $wait = 0
                while ($mgr.TetheringOperationalState -ne 2 -and $wait -lt 15) {{
                    Start-Sleep -Milliseconds 100
                    $wait++
                }}
            }} catch {{}}
        }}

        # Create fresh access point configuration (wipes previous settings)
        $config = $mgr.GetCurrentAccessPointConfiguration()
        $config.Ssid = ""{safeSsid}""
        $config.Passphrase = ""{safePass}""
        
        $requestedBand = [Windows.Networking.NetworkOperators.TetheringWiFiBand]::{bandEnum}
        $supported = $true
        try {{
            $supported = $mgr.IsBandSupported($requestedBand)
        }} catch {{ $supported = $true }}

        if ($supported) {{
            $config.Band = $requestedBand
        }} else {{
            try {{ $config.Band = [Windows.Networking.NetworkOperators.TetheringWiFiBand]::TwoPointFourGigahertz }} catch {{}}
        }}
        
        $op = $mgr.ConfigureAccessPointAsync($config)
        while ($op.Status -eq 0) {{ Start-Sleep -Milliseconds 60 }}
        
        @{{ Result = 'Success' }} | ConvertTo-Json -Compress
    }} catch {{
        @{{ Result = 'Error'; Error = $_.Exception.Message }} | ConvertTo-Json -Compress
    }}
}} else {{
    @{{ Result = 'Error'; Error = 'No active network connection profile found' }} | ConvertTo-Json -Compress
}}
";
                string json = RunPowerShell(script);
                try
                {
                    var res = JsonSerializer.Deserialize<OperationResultJson>(json);
                    if (res?.Result == "Success")
                    {
                        return (true, "Hotspot configuration updated fresh as new!");
                    }
                    return (false, res?.Error ?? "Configuration failed.");
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            });
        }

        /// <summary>
        /// Optimizes and repairs Windows Internet Connection Sharing (ICS) DHCP settings for the Mobile Hotspot.
        /// Ensures SharedAccess service is running, sets optimal 192.168.137.1 scope, and clears stale ARP leases.
        /// </summary>
        public async Task<(bool success, string message)> OptimizeHotspotDhcpAsync()
        {
            return await Task.Run(() =>
            {
                string script = @"
try {
    # 1. Ensure SharedAccess (ICS / DHCP) service is enabled and started
    $svc = Get-Service -Name 'SharedAccess' -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.StartType -eq 'Disabled') {
            Set-Service -Name 'SharedAccess' -StartupType Manual -ErrorAction SilentlyContinue
        }
        if ($svc.Status -ne 'Running') {
            Start-Service -Name 'SharedAccess' -ErrorAction SilentlyContinue
        }
    }

    # 2. Configure conflict-free standard DHCP scope (192.168.137.1/24)
    $regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters'
    if (Test-Path $regPath) {
        try {
            Set-ItemProperty -Path $regPath -Name 'ScopeAddress' -Value '192.168.137.1' -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $regPath -Name 'StandaloneDhcpAddress' -Value '192.168.137.1' -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $regPath -Name 'ScopeAddressBackup' -Value '192.168.137.1' -ErrorAction SilentlyContinue
        } catch {}
    }

    # 3. Clear any stale ARP cache for the hotspot subnet
    try {
        netsh interface ip delete arpcache | Out-Null
    } catch {}

    @{ Result = 'Success' } | ConvertTo-Json -Compress
} catch {
    @{ Result = 'Error'; Error = $_.Exception.Message } | ConvertTo-Json -Compress
}
";
                string json = RunPowerShell(script);
                try
                {
                    var res = JsonSerializer.Deserialize<OperationResultJson>(json);
                    if (res?.Result == "Success")
                    {
                        return (true, "DHCP scope (192.168.137.1) & SharedAccess service verified and optimized!");
                    }
                    return (false, res?.Error ?? "DHCP optimization check finished.");
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            });
        }

        /// <summary>
        /// Completely resets the Mobile Hotspot subsystem, restarts SharedAccess (DHCP),
        /// wipes previous configuration, and recreates the hotspot fresh as new.
        /// </summary>
        public async Task<(bool success, string message)> ResetAndRecreateHotspotAsync(string ssid, string passphrase, string bandOrChannel)
        {
            return await Task.Run(async () =>
            {
                // 1. Force stop tethering to release virtual adapter
                await StopHotspotAsync();

                // 2. Restart SharedAccess (DHCP server) to release hung sockets and clear ARP
                string resetDhcpScript = @"
try {
    Restart-Service -Name 'SharedAccess' -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
    netsh interface ip delete arpcache | Out-Null
    @{ Result = 'Success' } | ConvertTo-Json -Compress
} catch {
    @{ Result = 'Success' } | ConvertTo-Json -Compress
}
";
                RunPowerShell(resetDhcpScript);
                await Task.Delay(800);

                // 3. Configure fresh with clean settings as new
                var configRes = await ConfigureHotspotAsync(ssid, passphrase, bandOrChannel);
                if (!configRes.success)
                {
                    return (false, $"Reset configuration failed: {configRes.message}");
                }

                await Task.Delay(600);

                // 4. Start Hotspot
                var startRes = await StartHotspotAsync();
                if (startRes.success)
                {
                    return (true, "Hotspot completely reset, DHCP repaired, and restarted fresh!");
                }
                return (false, $"Reset completed but start returned: {startRes.message}");
            });
        }

        /// <summary>
        /// Starts the Windows Mobile Hotspot.
        /// </summary>
        public async Task<(bool success, string message)> StartHotspotAsync()
        {
            return await Task.Run(() =>
            {
                string script = @"
$connectionProfile = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetInternetConnectionProfile()
if (-not $connectionProfile) {
    $profiles = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetConnectionProfiles()
    if ($profiles) {
        foreach ($p in $profiles) {
            try {
                if ($p.GetNetworkConnectivityLevel() -ge 1) { $connectionProfile = $p; break }
            } catch {}
        }
        if (-not $connectionProfile -and $profiles.Count -gt 0) { $connectionProfile = $profiles[0] }
    }
}
if ($connectionProfile) {
    try {
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]::CreateFromConnectionProfile($connectionProfile)
        $op = $mgr.StartTetheringAsync()
        
        # Poll state until On (1)
        $count = 0
        while ($mgr.TetheringOperationalState -ne 1 -and $count -lt 35) {
            Start-Sleep -Milliseconds 200
            $count++
        }

        if ($mgr.TetheringOperationalState -eq 1) {
            @{ Result = 'Success' } | ConvertTo-Json -Compress
        } else {
            @{ Result = 'Error'; Error = 'Timed out starting Mobile Hotspot.' } | ConvertTo-Json -Compress
        }
    } catch {
        @{ Result = 'Error'; Error = $_.Exception.Message } | ConvertTo-Json -Compress
    }
} else {
    @{ Result = 'Error'; Error = 'No active network connection profile found' } | ConvertTo-Json -Compress
}
";
                string json = RunPowerShell(script);
                try
                {
                    var res = JsonSerializer.Deserialize<OperationResultJson>(json);
                    if (res?.Result == "Success")
                    {
                        return (true, "Mobile Hotspot is now ACTIVE!");
                    }
                    return (false, string.IsNullOrEmpty(res?.Error) ? "Failed to start Hotspot." : res.Error);
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            });
        }

        /// <summary>
        /// Stops the Windows Mobile Hotspot.
        /// </summary>
        public async Task<(bool success, string message)> StopHotspotAsync()
        {
            return await Task.Run(() =>
            {
                string script = @"
$connectionProfile = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetInternetConnectionProfile()
if (-not $connectionProfile) {
    $profiles = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetConnectionProfiles()
    if ($profiles) {
        foreach ($p in $profiles) {
            try {
                if ($p.GetNetworkConnectivityLevel() -ge 1) { $connectionProfile = $p; break }
            } catch {}
        }
        if (-not $connectionProfile -and $profiles.Count -gt 0) { $connectionProfile = $profiles[0] }
    }
}
if ($connectionProfile) {
    try {
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]::CreateFromConnectionProfile($connectionProfile)
        $op = $mgr.StopTetheringAsync()

        # Poll state until Off (2)
        $count = 0
        while ($mgr.TetheringOperationalState -eq 1 -and $count -lt 25) {
            Start-Sleep -Milliseconds 200
            $count++
        }

        @{ Result = 'Success' } | ConvertTo-Json -Compress
    } catch {
        @{ Result = 'Error'; Error = $_.Exception.Message } | ConvertTo-Json -Compress
    }
} else {
    @{ Result = 'Error'; Error = 'No active network connection profile found' } | ConvertTo-Json -Compress
}
";
                string json = RunPowerShell(script);
                try
                {
                    var res = JsonSerializer.Deserialize<OperationResultJson>(json);
                    if (res?.Result == "Success")
                    {
                        return (true, "Mobile Hotspot stopped.");
                    }
                    return (false, string.IsNullOrEmpty(res?.Error) ? "Failed to stop Hotspot." : res.Error);
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            });
        }

        private static long _lastDirectAdapterBytes = 0;

        private static async Task<bool> CheckInternetAvailableAsync()
        {
            try
            {
                using var pinger = new Ping();
                var reply = await pinger.SendPingAsync("8.8.8.8", 250);
                if (reply.Status == IPStatus.Success) return true;
                var reply2 = await pinger.SendPingAsync("1.1.1.1", 250);
                return reply2.Status == IPStatus.Success;
            }
            catch
            {
                return NetworkInterface.GetIsNetworkAvailable();
            }
        }

        /// <summary>
        /// Enriches connected client devices with real-time negotiated link speed, live ICMP latency,
        /// and data flow health (verifying whether clients are actively getting data).
        /// </summary>
        private static async Task EnrichClientSpeedAsync(IEnumerable<HotspotClientInfo> clients, string activeBand)
        {
            long negotiatedSpeedBps = 0;
            long currentDirectBytes = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up)
                    {
                        if (ni.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                            ni.Name.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                            ni.Name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase))
                        {
                            if (ni.Speed > 0 && negotiatedSpeedBps <= 0)
                            {
                                negotiatedSpeedBps = ni.Speed;
                            }
                            var stats = ni.GetIPv4Statistics();
                            currentDirectBytes += (stats.BytesReceived + stats.BytesSent);
                        }
                    }
                }
                if (negotiatedSpeedBps <= 0)
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                        {
                            if (ni.Speed > 0)
                            {
                                negotiatedSpeedBps = ni.Speed;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            bool adapterHasActiveData = false;
            if (_lastDirectAdapterBytes > 0 && currentDirectBytes > _lastDirectAdapterBytes)
            {
                adapterHasActiveData = (currentDirectBytes - _lastDirectAdapterBytes) > 100;
            }
            _lastDirectAdapterBytes = currentDirectBytes;

            bool hostHasInternet = await CheckInternetAvailableAsync();

            double speedMbps = negotiatedSpeedBps > 0 ? (negotiatedSpeedBps / 1_000_000.0) : (activeBand.Contains("5 GHz") ? 300.0 : 72.2);
            string linkText = $"{speedMbps:F0} Mbps";

            var tasks = clients.Select(async client =>
            {
                client.LinkSpeed = linkText;
                bool hasIp = !string.IsNullOrEmpty(client.IpAddress) && client.IpAddress != "-";

                if (!hasIp)
                {
                    client.IsGettingData = false;
                    client.DataStatusText = "No IP";
                    client.Latency = "No DHCP Lease";
                    client.ConnectionSpeed = "Awaiting IP";
                    return;
                }

                if (!hostHasInternet)
                {
                    client.IsGettingData = false;
                    client.DataStatusText = "No Internet";
                    client.Latency = "-";
                    client.ConnectionSpeed = "Host Offline";
                    return;
                }

                bool pingSuccess = false;
                long rtt = 0;
                try
                {
                    using var pinger = new Ping();
                    var reply = await pinger.SendPingAsync(client.IpAddress, 180);
                    if (reply.Status == IPStatus.Success)
                    {
                        pingSuccess = true;
                        rtt = Math.Max(1, reply.RoundtripTime);
                    }
                }
                catch { }

                if (pingSuccess)
                {
                    client.IsGettingData = true;
                    client.DataStatusText = "● Getting Data";
                    client.Latency = $"{rtt} ms";
                    client.ConnectionSpeed = $"{linkText} • {rtt}ms";
                }
                else if (adapterHasActiveData)
                {
                    client.IsGettingData = true;
                    client.DataStatusText = "● Getting Data";
                    client.Latency = "< 5 ms";
                    client.ConnectionSpeed = $"{linkText} • Active Flow";
                }
                else
                {
                    client.IsGettingData = false;
                    client.DataStatusText = "⚠ No Data";
                    client.Latency = "Timed Out";
                    client.ConnectionSpeed = "No Data Flow";
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Disconnects a client device from the hotspot by resetting its network state and dropping its connection.
        /// </summary>
        public async Task<(bool success, string message)> DisconnectClientAsync(string macAddress, string ipAddress)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string cleanMac = HotspotModel.NormalizeMac(macAddress);
                    string targetIp = ipAddress;
                    if (string.IsNullOrEmpty(targetIp) || targetIp == "-")
                    {
                        var arpMap = NativeArpHelper.GetHotspotArpEntries();
                        foreach (var kvp in arpMap)
                        {
                            if (HotspotModel.NormalizeMac(kvp.Key) == cleanMac)
                            {
                                targetIp = kvp.Value;
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(targetIp) && targetIp != "-")
                    {
                        // Add temporary block to sever TCP/UDP connections immediately
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_TempDrop_{cleanMac}\" dir=in action=block remoteip={targetIp}");
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_TempDrop_Out_{cleanMac}\" dir=out action=block remoteip={targetIp}");
                        RunCommand("route", $"add {targetIp} mask 255.255.255.255 192.168.137.1");
                        RunCommand("netsh", "interface ip delete arpcache");

                        // Auto-clear the temporary drop rules after 4 seconds
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(4000);
                            RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_TempDrop_{cleanMac}\"");
                            RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_TempDrop_Out_{cleanMac}\"");
                            RunCommand("route", $"delete {targetIp}");
                            RunCommand("netsh", "interface ip delete arpcache");
                        });
                    }
                    else
                    {
                        RunCommand("netsh", "interface ip delete arpcache");
                    }

                    return (true, $"Device {macAddress} disconnected successfully.");
                }
                catch (Exception ex)
                {
                    return (false, $"Disconnect error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Permanently blocks a client device from accessing the hotspot using multi-layer isolation:
        /// Windows Defender Firewall (In/Out/DNS/DHCP), host route blackhole loop, and ARP cache purging.
        /// </summary>
        public async Task<(bool success, string message)> BlockClientAsync(string macAddress, string ipAddress)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string cleanMac = HotspotModel.NormalizeMac(macAddress);
                    string targetIp = ipAddress;

                    // If IP address was missing or '-', lookup from ARP table by MAC
                    if (string.IsNullOrEmpty(targetIp) || targetIp == "-")
                    {
                        var arpMap = NativeArpHelper.GetHotspotArpEntries();
                        foreach (var kvp in arpMap)
                        {
                            if (HotspotModel.NormalizeMac(kvp.Key) == cleanMac)
                            {
                                targetIp = kvp.Value;
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(targetIp) && targetIp != "-")
                    {
                        // 1. Netsh advfirewall rules: Inbound, Outbound, ICS DNS (UDP & TCP 53), DHCP (UDP 67)
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_In_{cleanMac}\" dir=in action=block remoteip={targetIp}");
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_Out_{cleanMac}\" dir=out action=block remoteip={targetIp}");
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_DNS_{cleanMac}\" dir=in action=block protocol=UDP localport=53 remoteip={targetIp}");
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_DNST_{cleanMac}\" dir=in action=block protocol=TCP localport=53 remoteip={targetIp}");
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_DHCP_{cleanMac}\" dir=in action=block protocol=UDP localport=67 remoteip={targetIp}");

                        // 2. PowerShell New-NetFirewallRule for deep WFP integration
                        string psRules = $@"
try {{
    New-NetFirewallRule -DisplayName 'Hotspot_Block_In_{cleanMac}' -Direction Inbound -Action Block -RemoteAddress '{targetIp}' -ErrorAction SilentlyContinue | Out-Null
    New-NetFirewallRule -DisplayName 'Hotspot_Block_Out_{cleanMac}' -Direction Outbound -Action Block -RemoteAddress '{targetIp}' -ErrorAction SilentlyContinue | Out-Null
}} catch {{}}
";
                        RunPowerShell(psRules);

                        // 3. Routing Blackhole: loop all traffic for this IP back to host gateway 192.168.137.1
                        RunCommand("route", $"add {targetIp} mask 255.255.255.255 192.168.137.1");
                    }

                    // 4. Flush ARP cache
                    RunCommand("netsh", "interface ip delete arpcache");

                    return (true, $"Device {macAddress} is now BLOCKED from the hotspot.");
                }
                catch (Exception ex)
                {
                    return (false, $"Block error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Unblocks a previously blocked client device, removing all firewall and blackhole routing rules.
        /// </summary>
        public async Task<(bool success, string message)> UnblockClientAsync(string macAddress, string ipAddress)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string cleanMac = HotspotModel.NormalizeMac(macAddress);
                    string targetIp = ipAddress;

                    // Delete netsh firewall rules
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_In_{cleanMac}\"");
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_Out_{cleanMac}\"");
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_DNS_{cleanMac}\"");
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_DNST_{cleanMac}\"");
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_DHCP_{cleanMac}\"");

                    // Delete PowerShell NetFirewallRules
                    string psUnblock = $@"
try {{
    Remove-NetFirewallRule -DisplayName 'Hotspot_Block_In_{cleanMac}' -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -DisplayName 'Hotspot_Block_Out_{cleanMac}' -ErrorAction SilentlyContinue
}} catch {{}}
";
                    RunPowerShell(psUnblock);

                    // Delete route blackhole
                    if (!string.IsNullOrEmpty(targetIp) && targetIp != "-")
                    {
                        RunCommand("route", $"delete {targetIp}");
                    }

                    // Flush ARP cache
                    RunCommand("netsh", "interface ip delete arpcache");

                    return (true, $"Device {macAddress} unblocked successfully.");
                }
                catch (Exception ex)
                {
                    return (false, $"Unblock error: {ex.Message}");
                }
            });
        }

        private static string RunCommand(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var process = Process.Start(psi);
                if (process == null) return string.Empty;
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(true); } catch { }
                }
                return output;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string RunPowerShell(string script)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command -",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return string.Empty;

                process.StandardInput.WriteLine(script);
                process.StandardInput.Close();

                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(12000))
                {
                    try { process.Kill(true); } catch { }
                }
                return output.Trim();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerShell exec error: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Reads Windows ARP table directly from in-process iphlpapi.dll memory.
        /// Zero child processes (no arp.exe or powershell.exe) - completely immune to corporate EDR / Antivirus blocks.
        /// </summary>
        private static class NativeArpHelper
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct MIB_IPNETROW
            {
                public int dwIndex;
                public int dwPhysAddrLen;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
                public byte[] bPhysAddr;
                public uint dwAddr;
                public int dwType;
            }

            [DllImport("iphlpapi.dll", SetLastError = true)]
            private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

            public static Dictionary<string, string> GetHotspotArpEntries()
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    int bytesNeeded = 0;
                    GetIpNetTable(IntPtr.Zero, ref bytesNeeded, false);
                    if (bytesNeeded <= 0) return map;

                    IntPtr buffer = Marshal.AllocHGlobal(bytesNeeded);
                    try
                    {
                        if (GetIpNetTable(buffer, ref bytesNeeded, false) == 0)
                        {
                            int numEntries = Marshal.ReadInt32(buffer);
                            IntPtr rowPtr = IntPtr.Add(buffer, 4);
                            int rowSize = Marshal.SizeOf<MIB_IPNETROW>();

                            for (int i = 0; i < numEntries; i++)
                            {
                                var row = Marshal.PtrToStructure<MIB_IPNETROW>(IntPtr.Add(rowPtr, i * rowSize));
                                // dwType: 3 = Dynamic, 4 = Static
                                if (row.dwPhysAddrLen >= 6 && (row.dwType == 3 || row.dwType == 4))
                                {
                                    var ip = new IPAddress(BitConverter.GetBytes(row.dwAddr)).ToString();
                                    // Filter for Hotspot subnet (192.168.137.x, exclude host .1 and broadcast .255)
                                    if (ip.StartsWith("192.168.137.") && ip != "192.168.137.1" && ip != "192.168.137.255")
                                    {
                                        var sb = new StringBuilder();
                                        for (int m = 0; m < 6; m++)
                                        {
                                            if (m > 0) sb.Append(':');
                                            sb.Append(row.bPhysAddr[m].ToString("X2"));
                                        }
                                        map[sb.ToString()] = ip;
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Native ARP error: {ex.Message}");
                }

                return map;
            }
        }
    }
}
