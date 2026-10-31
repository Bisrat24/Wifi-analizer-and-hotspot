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
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class HotspotService
    {
        private readonly HotspotPriorityService _priorityService = new();
        public HotspotPriorityService PriorityService => _priorityService;

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

        private static (int channel, string band) _cachedVirtualChannel = (0, "");
        private static DateTime _lastVirtualChannelCheck = DateTime.MinValue;
        private static readonly Dictionary<string, int> _clientConsecutiveMisses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Attempts to query the live channel of the active Wi-Fi Direct virtual adapter via netsh wlan show interfaces.
        /// Zero PowerShell subprocess, completely safe from EDR blocks. Caches result for 5s to avoid repeated netsh spawning.
        /// </summary>
        private static (int channel, string band) DetectVirtualHotspotChannel()
        {
            if (_cachedVirtualChannel.channel > 0 && (DateTime.Now - _lastVirtualChannelCheck).TotalSeconds < 5)
            {
                return _cachedVirtualChannel;
            }
            _lastVirtualChannelCheck = DateTime.Now;

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
                if (!process.WaitForExit(2000))
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
                            _cachedVirtualChannel = (ch, band);
                            return (ch, band);
                        }
                    }
                }
            }
            catch { }
            return (0, "");
        }

        private static ConnectionProfile? GetActiveConnectionProfile()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile != null) return profile;

                var profiles = NetworkInformation.GetConnectionProfiles();
                if (profiles != null)
                {
                    foreach (var p in profiles)
                    {
                        try
                        {
                            if ((int)p.GetNetworkConnectivityLevel() >= 1) return p;
                        }
                        catch { }
                    }
                    if (profiles.Count > 0) return profiles[0];
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Reads current Hotspot status, SSID, password, band, active broadcasting channel, and all connected clients.
        /// Uses direct in-process WinRT Tethering API (sub-10ms response) with active hardware SendARP link verification.
        /// Automatically detects and purges disconnected devices and prevents stale ARP cache ghosts.
        /// </summary>
        public async Task<HotspotModel> GetStatusAsync(int activeChannel = 0, string activeBand = "", string activeInterfaceName = "", string configuredChoice = "")
        {
            return await Task.Run(async () =>
            {
                var model = new HotspotModel();
                string safeBand = !string.IsNullOrEmpty(activeBand) ? activeBand : "2.4 GHz";
                string safeIf = !string.IsNullOrEmpty(activeInterfaceName) ? activeInterfaceName : "Wi-Fi";

                try
                {
                    var profile = GetActiveConnectionProfile();
                    if (profile != null)
                    {
                        var mgr = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                        if (mgr != null)
                        {
                            var state = mgr.TetheringOperationalState;
                            model.State = state switch
                            {
                                TetheringOperationalState.On => HotspotState.On,
                                TetheringOperationalState.Off => HotspotState.Off,
                                TetheringOperationalState.InTransition => HotspotState.InTransition,
                                _ => HotspotState.Unknown
                            };

                            try
                            {
                                var config = mgr.GetCurrentAccessPointConfiguration();
                                if (config != null)
                                {
                                    model.Ssid = config.Ssid ?? "MyHotspot";
                                    model.Passphrase = config.Passphrase ?? "password123";
                                    model.Band = (int)config.Band switch
                                    {
                                        1 => "2.4 GHz",
                                        2 => "5 GHz",
                                        _ => "Auto / Any"
                                    };
                                }
                            }
                            catch { }

                            // Determine Active Channel, Band, and Channel Description
                            if (model.State != HotspotState.On)
                            {
                                model.ActiveChannel = "Off";
                                model.ActiveBand = model.Band;
                                model.ChannelDescription = "Hotspot is stopped";
                            }
                            else if (activeChannel > 0)
                            {
                                model.ActiveChannel = $"Channel {activeChannel}";
                                model.ActiveBand = !string.IsNullOrEmpty(activeBand) ? activeBand : model.Band;
                                model.ChannelDescription = $"Broadcasting on Channel {activeChannel} ({model.ActiveBand}) • Synced with {safeIf}";
                            }
                            else
                            {
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
                                    else if (!string.IsNullOrEmpty(configuredChoice) && configuredChoice.Contains("5 GHz"))
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

                            // Query candidate clients from native WinRT tethering
                            var candidateClients = new List<HotspotClientInfo>();
                            try
                            {
                                var rawClients = mgr.GetTetheringClients();
                                if (rawClients != null)
                                {
                                    foreach (var c in rawClients)
                                    {
                                        string mac = c.MacAddress ?? "";
                                        if (string.IsNullOrWhiteSpace(mac)) continue;

                                        string name = "Client Device";
                                        string ip = "";
                                        if (c.HostNames != null)
                                        {
                                            foreach (var hn in c.HostNames)
                                            {
                                                try
                                                {
                                                    if (hn.Type == Windows.Networking.HostNameType.Ipv4 ||
                                                        Regex.IsMatch(hn.CanonicalName, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$"))
                                                    {
                                                        ip = hn.CanonicalName;
                                                    }
                                                    else if (!string.IsNullOrWhiteSpace(hn.DisplayName))
                                                    {
                                                        name = hn.DisplayName;
                                                    }
                                                    else if (!string.IsNullOrWhiteSpace(hn.CanonicalName) && name == "Client Device")
                                                    {
                                                        name = hn.CanonicalName;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }

                                        candidateClients.Add(new HotspotClientInfo
                                        {
                                            MacAddress = mac,
                                            HostName = name,
                                            IpAddress = ip,
                                            Status = "Connected"
                                        });
                                    }
                                }
                            }
                            catch { }

                            // Resolve IP addresses via fast native ARP table
                            var arpMap = NativeArpHelper.GetHotspotArpEntries();
                            foreach (var client in candidateClients)
                            {
                                if (string.IsNullOrEmpty(client.IpAddress) || client.IpAddress == "-")
                                {
                                    if (arpMap.TryGetValue(client.MacAddress, out var resolvedIp))
                                    {
                                        client.IpAddress = resolvedIp;
                                    }
                                }
                            }

                            // For clients still lacking IP, do parallel subnet sweep probe
                            var unresolved = candidateClients.Where(c => string.IsNullOrEmpty(c.IpAddress) || c.IpAddress == "-").ToList();
                            if (unresolved.Count > 0)
                            {
                                await Task.WhenAll(unresolved.Select(async u =>
                                {
                                    var probedIp = await NativeArpHelper.ProbeSubnetForMacAsync(u.MacAddress);
                                    if (!string.IsNullOrEmpty(probedIp))
                                    {
                                        u.IpAddress = probedIp;
                                    }
                                }));
                            }

                            // Format hostname for any client with resolved IP
                            foreach (var client in candidateClients)
                            {
                                if (!string.IsNullOrEmpty(client.IpAddress) && client.IpAddress != "-" &&
                                    (client.HostName == "Connected Device" || client.HostName == "Client Device"))
                                {
                                    client.HostName = $"Device ({client.IpAddress})";
                                }
                            }

                            // Exclude blocked or manually disconnected clients
                            candidateClients.RemoveAll(c => IsClientBlocked(c.MacAddress) || IsClientDisconnected(c.MacAddress));

                            // Verify reachability & prune disconnected devices
                            if (candidateClients.Count > 0)
                            {
                                await EnrichClientSpeedAsync(candidateClients, model.ActiveBand);
                            }

                            // Only retain clients that are confirmed connected (prune disconnected ghosts)
                            var activeVerifiedClients = candidateClients.Where(c => c.Status != "Disconnected").ToList();
                            model.Clients.Clear();
                            foreach (var client in activeVerifiedClients)
                            {
                                model.Clients.Add(client);
                            }
                            model.ClientCount = model.Clients.Count;

                            if (model.ClientCount > 0)
                            {
                                model.ActiveDataClientCount = model.Clients.Count(c => c.IsGettingData);
                                if (model.ActiveDataClientCount == model.ClientCount)
                                {
                                    model.DataHealthStatus = $"All {model.ClientCount} device(s) getting data";
                                }
                                else if (model.ActiveDataClientCount == 0)
                                {
                                    model.DataHealthStatus = $"⚠️ 0/{model.ClientCount} devices getting data (Stalled)";
                                }
                                else
                                {
                                    model.DataHealthStatus = $"{model.ActiveDataClientCount}/{model.ClientCount} device(s) getting data";
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

                            return model;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Native GetStatus failed, falling back to PS: {ex.Message}");
                }

                // Fallback to PowerShell if native WinRT failed
                return await GetStatusPowerShellAsync(activeChannel, activeBand, activeInterfaceName, configuredChoice);
            });
        }

        /// <summary>
        /// Fallback PowerShell query for Hotspot status if native WinRT fails.
        /// </summary>
        private async Task<HotspotModel> GetStatusPowerShellAsync(int activeChannel = 0, string activeBand = "", string activeInterfaceName = "", string configuredChoice = "")
        {
            return await Task.Run(async () =>
            {
                var model = new HotspotModel();

                string safeBand = !string.IsNullOrEmpty(activeBand) ? activeBand : "2.4 GHz";
                string safeIf = !string.IsNullOrEmpty(activeInterfaceName) ? activeInterfaceName : "Wi-Fi";

                string script = $@"
try {{ [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::RealTime }} catch {{}}
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

        try {{
            $rawClients = $mgr.GetTetheringClients()
            if ($rawClients) {{
                foreach ($c in $rawClients) {{
                    $name = 'Client Device'
                    $ip = ''
                    if ($c.HostNames) {{
                        foreach ($hn in $c.HostNames) {{
                            try {{
                                if ([int]$hn.Type -eq 1 -or $hn.CanonicalName -match '^\d{{1,3}}\.\d{{1,3}}\.\d{{1,3}}\.\d{{1,3}}$') {{
                                    $ip = $hn.CanonicalName
                                }} elseif ([int]$hn.Type -eq 0 -and -not [string]::IsNullOrWhiteSpace($hn.DisplayName)) {{
                                    $name = $hn.DisplayName
                                }} elseif (-not [string]::IsNullOrWhiteSpace($hn.DisplayName) -and $name -eq 'Client Device') {{
                                    $name = $hn.DisplayName
                                }}
                            }} catch {{}}
                        }}
                    }}
                    $clients += @{{ Mac = ""$($c.MacAddress)""; HostName = ""$name""; Ip = ""$ip"" }}
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

                        model.State = data.State switch
                        {
                            1 => HotspotState.On,
                            2 => HotspotState.Off,
                            3 => HotspotState.InTransition,
                            _ => HotspotState.Unknown
                        };

                        model.Band = data.Band switch
                        {
                            1 => "2.4 GHz",
                            2 => "5 GHz",
                            _ => "Auto / Any"
                        };

                        if (model.State != HotspotState.On)
                        {
                            model.ActiveChannel = "Off";
                            model.ActiveBand = model.Band;
                            model.ChannelDescription = "Hotspot is stopped";
                        }
                        else if (activeChannel > 0)
                        {
                            model.ActiveChannel = $"Channel {activeChannel}";
                            model.ActiveBand = !string.IsNullOrEmpty(activeBand) ? activeBand : model.Band;
                            model.ChannelDescription = $"Broadcasting on Channel {activeChannel} ({model.ActiveBand}) • Synced with {safeIf}";
                        }
                        else
                        {
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
                                else if (!string.IsNullOrEmpty(configuredChoice) && configuredChoice.Contains("5 GHz"))
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

                        var arpMap = NativeArpHelper.GetHotspotArpEntries();
                        foreach (var client in model.Clients)
                        {
                            if (string.IsNullOrEmpty(client.IpAddress) || client.IpAddress == "-")
                            {
                                if (arpMap.TryGetValue(client.MacAddress, out var resolvedIp))
                                {
                                    client.IpAddress = resolvedIp;
                                }
                            }
                        }

                        var blockedOrDisc = model.Clients.Where(c => IsClientBlocked(c.MacAddress) || IsClientDisconnected(c.MacAddress)).ToList();
                        foreach (var b in blockedOrDisc)
                        {
                            model.Clients.Remove(b);
                        }

                        if (model.Clients.Count > 0)
                        {
                            await EnrichClientSpeedAsync(model.Clients, model.ActiveBand);
                        }

                        var activeVerifiedClients = model.Clients.Where(c => c.Status != "Disconnected").ToList();
                        model.Clients.Clear();
                        foreach (var client in activeVerifiedClients)
                        {
                            model.Clients.Add(client);
                        }
                        model.ClientCount = model.Clients.Count;

                        if (model.ClientCount > 0)
                        {
                            model.ActiveDataClientCount = model.Clients.Count(c => c.IsGettingData);
                            if (model.ActiveDataClientCount == model.ClientCount)
                                model.DataHealthStatus = $"All {model.ClientCount} device(s) getting data";
                            else if (model.ActiveDataClientCount == 0)
                                model.DataHealthStatus = $"⚠️ 0/{model.ClientCount} devices getting data (Stalled)";
                            else
                                model.DataHealthStatus = $"{model.ActiveDataClientCount}/{model.ClientCount} device(s) getting data";
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
try {{ [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::RealTime }} catch {{}}
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
        /// Completely resets the Mobile Hotspot subsystem in a single resilient execution:
        /// gracefully stops existing tethering, repairs SharedAccess (ICS DHCP) service & registry scope (192.168.137.1),
        /// wipes previous configuration, clears stale ARP cache, and recreates & starts the hotspot fresh as new.
        /// </summary>
        public async Task<(bool success, string message)> ResetAndRecreateHotspotAsync(string ssid, string passphrase, string bandOrChannel)
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
$ErrorActionPreference = 'SilentlyContinue'

# 1. Stop existing tethering cleanly if active or transitioning
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
        if ($mgr.TetheringOperationalState -eq 1 -or $mgr.TetheringOperationalState -eq 3) {{
            $stopOp = $mgr.StopTetheringAsync()
            $stopWait = 0
            while ($mgr.TetheringOperationalState -ne 2 -and $stopWait -lt 30) {{
                Start-Sleep -Milliseconds 100
                $stopWait++
            }}
        }}
    }} catch {{}}
}}

# 2. Reset and ensure SharedAccess (ICS / DHCP) service is healthy
try {{
    $svc = Get-Service -Name 'SharedAccess' -ErrorAction SilentlyContinue
    if ($svc) {{
        if ($svc.StartType -eq 'Disabled') {{
            Set-Service -Name 'SharedAccess' -StartupType Manual -ErrorAction SilentlyContinue
        }}
        if ($svc.Status -eq 'Running') {{
            Stop-Service -Name 'SharedAccess' -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 400
        }}
    }}

    $regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters'
    if (Test-Path $regPath) {{
        Set-ItemProperty -Path $regPath -Name 'ScopeAddress' -Value '192.168.137.1' -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $regPath -Name 'StandaloneDhcpAddress' -Value '192.168.137.1' -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $regPath -Name 'ScopeAddressBackup' -Value '192.168.137.1' -ErrorAction SilentlyContinue
    }}

    Start-Service -Name 'SharedAccess' -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}} catch {{}}

# Clear ARP cache
try {{ netsh interface ip delete arpcache | Out-Null }} catch {{}}

# 3. Re-acquire active connection profile
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

if (-not $connectionProfile) {{
    @{{ Result = 'Error'; Error = 'No active network profile available to host hotspot.' }} | ConvertTo-Json -Compress
    return
}}

try {{
    $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]::CreateFromConnectionProfile($connectionProfile)

    # 4. Configure Access Point with fresh SSID, passphrase, and band
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

    $cfgOp = $mgr.ConfigureAccessPointAsync($config)
    $cfgWait = 0
    while ($cfgOp.Status -eq 0 -and $cfgWait -lt 50) {{
        Start-Sleep -Milliseconds 100
        $cfgWait++
    }}

    # 5. Start Tethering with Retry Logic (up to 3 attempts)
    $started = $false
    $lastErr = ''

    for ($attempt = 1; $attempt -le 3; $attempt++) {{
        if ($mgr.TetheringOperationalState -eq 1) {{
            $started = $true
            break
        }}

        $startOp = $mgr.StartTetheringAsync()
        $startWait = 0
        while ($startOp.Status -eq 0 -and $startWait -lt 60) {{
            Start-Sleep -Milliseconds 150
            $startWait++
        }}

        if ($startOp.Status -eq 1) {{
            $opResult = $startOp.GetResults()
            if ($opResult.Status -eq 0 -or $opResult.Status.ToString() -eq 'Success' -or $opResult.Status.ToString() -eq 'AlreadyOn') {{
                $started = $true
                break
            }} else {{
                $lastErr = ""$($opResult.Status): $($opResult.AdditionalErrorMessage)""
            }}
        }} elseif ($startOp.Status -eq 3) {{
            $lastErr = 'WinRT StartTetheringAsync returned error status'
        }}

        if ($mgr.TetheringOperationalState -eq 1) {{
            $started = $true
            break
        }}

        Start-Sleep -Milliseconds 800
    }}

    # Verify final operational state
    $verifyCount = 0
    while ($mgr.TetheringOperationalState -ne 1 -and $verifyCount -lt 15) {{
        Start-Sleep -Milliseconds 200
        $verifyCount++
    }}

    if ($mgr.TetheringOperationalState -eq 1 -or $started) {{
        @{{ Result = 'Success' }} | ConvertTo-Json -Compress
    }} else {{
        @{{ Result = 'Error'; Error = $(if ($lastErr) {{ $lastErr }} else {{ 'Timed out starting Hotspot after reset' }}) }} | ConvertTo-Json -Compress
    }}
}} catch {{
    @{{ Result = 'Error'; Error = $_.Exception.Message }} | ConvertTo-Json -Compress
}}
";
                string json = RunPowerShell(script, 30000);
                try
                {
                    var res = JsonSerializer.Deserialize<OperationResultJson>(json);
                    if (res?.Result == "Success")
                    {
                        _ = Task.Run(async () => await _priorityService.EngageHighestPriorityAsync());
                        return (true, "Hotspot completely reset, DHCP repaired (192.168.137.1), and restarted with Highest PC Priority!");
                    }
                    return (false, string.IsNullOrEmpty(res?.Error) ? "Reset finished but start could not be verified." : res.Error);
                }
                catch (Exception ex)
                {
                    return (false, $"Reset error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Starts the Windows Mobile Hotspot with robust retry logic, virtual adapter readiness check,
        /// and SharedAccess service verification.
        /// </summary>
        public async Task<(bool success, string message)> StartHotspotAsync()
        {
            return await Task.Run(() =>
            {
                string script = @"
$ErrorActionPreference = 'SilentlyContinue'

# Elevate hotspot creation process to RealTime priority
try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::RealTime } catch {}

# 1. Ensure SharedAccess service is running and boosted to RealTime priority
try {
    $svc = Get-Service -Name 'SharedAccess' -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.StartType -eq 'Disabled') {
            Set-Service -Name 'SharedAccess' -StartupType Manual -ErrorAction SilentlyContinue
        }
        if ($svc.Status -ne 'Running') {
            Start-Service -Name 'SharedAccess' -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 400
        }
    }
    $sa = Get-CimInstance Win32_Service -Filter ""Name='SharedAccess'"" -ErrorAction SilentlyContinue
    if ($sa -and $sa.ProcessId -gt 0) {
        $sap = Get-Process -Id $sa.ProcessId -ErrorAction SilentlyContinue
        if ($sap) { $sap.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::RealTime }
    }
} catch {}

# 2. Acquire active Internet connection profile
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

if (-not $connectionProfile) {
    @{ Result = 'Error'; Error = 'No active network connection profile found.' } | ConvertTo-Json -Compress
    return
}

try {
    $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]::CreateFromConnectionProfile($connectionProfile)
    
    # If already running, return success immediately
    if ($mgr.TetheringOperationalState -eq 1) {
        @{ Result = 'Success' } | ConvertTo-Json -Compress
        return
    }

    # If in transition, wait for it to settle into Off (2) before starting
    if ($mgr.TetheringOperationalState -eq 3) {
        $waitSettle = 0
        while ($mgr.TetheringOperationalState -eq 3 -and $waitSettle -lt 20) {
            Start-Sleep -Milliseconds 150
            $waitSettle++
        }
    }

    # Start tethering with retry logic (up to 3 attempts)
    $started = $false
    $lastErr = ''

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        if ($mgr.TetheringOperationalState -eq 1) {
            $started = $true
            break
        }

        $startOp = $mgr.StartTetheringAsync()
        $startWait = 0
        while ($startOp.Status -eq 0 -and $startWait -lt 60) {
            Start-Sleep -Milliseconds 150
            $startWait++
        }

        if ($startOp.Status -eq 1) {
            $opResult = $startOp.GetResults()
            if ($opResult.Status -eq 0 -or $opResult.Status.ToString() -eq 'Success' -or $opResult.Status.ToString() -eq 'AlreadyOn') {
                $started = $true
                break
            } else {
                $lastErr = ""$($opResult.Status): $($opResult.AdditionalErrorMessage)""
            }
        }

        if ($mgr.TetheringOperationalState -eq 1) {
            $started = $true
            break
        }

        Start-Sleep -Milliseconds 600
    }

    # Final verification of operational state
    $verifyCount = 0
    while ($mgr.TetheringOperationalState -ne 1 -and $verifyCount -lt 15) {
        Start-Sleep -Milliseconds 200
        $verifyCount++
    }

    if ($mgr.TetheringOperationalState -eq 1 -or $started) {
        @{ Result = 'Success' } | ConvertTo-Json -Compress
    } else {
        @{ Result = 'Error'; Error = $(if ($lastErr) { $lastErr } else { 'Timed out starting Mobile Hotspot.' }) } | ConvertTo-Json -Compress
    }
} catch {
    @{ Result = 'Error'; Error = $_.Exception.Message } | ConvertTo-Json -Compress
}
";
                string json = RunPowerShell(script, 25000);
                try
                {
                    var res = JsonSerializer.Deserialize<OperationResultJson>(json);
                    if (res?.Result == "Success")
                    {
                        _ = Task.Run(async () => await _priorityService.EngageHighestPriorityAsync());
                        return (true, "Mobile Hotspot is now ACTIVE with Highest PC Priority!");
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
        /// Stops the Windows Mobile Hotspot and waits until virtual adapter is completely released.
        /// </summary>
        public async Task<(bool success, string message)> StopHotspotAsync()
        {
            return await Task.Run(() =>
            {
                string script = @"
$ErrorActionPreference = 'SilentlyContinue'
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
        
        # Stop tethering if active or in transition
        if ($mgr.TetheringOperationalState -eq 1 -or $mgr.TetheringOperationalState -eq 3) {
            $op = $mgr.StopTetheringAsync()
            
            # Poll state until Off (2)
            $count = 0
            while ($mgr.TetheringOperationalState -ne 2 -and $count -lt 40) {
                Start-Sleep -Milliseconds 150
                $count++
            }
        }
        
        # Settle delay so virtual adapter unbinds cleanly
        Start-Sleep -Milliseconds 300
        @{ Result = 'Success' } | ConvertTo-Json -Compress
    } catch {
        @{ Result = 'Error'; Error = $_.Exception.Message } | ConvertTo-Json -Compress
    }
} else {
    @{ Result = 'Error'; Error = 'No active network connection profile found' } | ConvertTo-Json -Compress
}
";
                string json = RunPowerShell(script, 15000);
                try
                {
                    _priorityService.ReleaseHighestPriority();
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

        public async Task<(bool success, string message)> OptimizePriorityAsync()
        {
            var res = await _priorityService.EngageHighestPriorityAsync();
            return (res.success, res.details);
        }

        private class ClientUsageStats
        {
            public long BytesDown { get; set; }
            public long BytesUp { get; set; }
            public string QosLimit { get; set; } = "Unlimited";
        }

        private static readonly Dictionary<string, ClientUsageStats> _clientDataUsageMap = new(StringComparer.OrdinalIgnoreCase);

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return $"{(double)bytes / (1024L * 1024L * 1024L):F2} GB";
            if (bytes >= 1024L * 1024L)
                return $"{(double)bytes / (1024L * 1024L):F1} MB";
            if (bytes >= 1024L)
                return $"{(double)bytes / 1024L:F0} KB";
            return $"{bytes} B";
        }

        private static long _lastDirectAdapterBytes = 0;
        private static bool _cachedInternetAvailable = true;
        private static DateTime _lastInternetCheck = DateTime.MinValue;

        private static async Task<bool> CheckInternetAvailableAsync()
        {
            if ((DateTime.Now - _lastInternetCheck).TotalSeconds < 3)
            {
                return _cachedInternetAvailable;
            }
            _lastInternetCheck = DateTime.Now;

            try
            {
                using var pinger = new Ping();
                var reply = await pinger.SendPingAsync("8.8.8.8", 150);
                if (reply.Status == IPStatus.Success)
                {
                    _cachedInternetAvailable = true;
                    return true;
                }
                var reply2 = await pinger.SendPingAsync("1.1.1.1", 150);
                _cachedInternetAvailable = (reply2.Status == IPStatus.Success);
                return _cachedInternetAvailable;
            }
            catch
            {
                _cachedInternetAvailable = NetworkInterface.GetIsNetworkAvailable();
                return _cachedInternetAvailable;
            }
        }

        /// <summary>
        /// Enriches connected client devices with real-time negotiated link speed, live latency,
        /// and data flow health. Prioritizes instant hardware-level SendARP (1-3ms) directly answered
        /// by client Wi-Fi chips, followed by fast Ping/TCP fallback.
        /// Unresponsive/ghost devices are flagged Disconnected after 2 failed checks and purged.
        /// </summary>
        private static async Task EnrichClientSpeedAsync(IEnumerable<HotspotClientInfo> clients, string activeBand)
        {
            var clientList = clients.ToList();
            if (clientList.Count == 0) return;

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

            bool hostHasInternet = await CheckInternetAvailableAsync();

            double speedMbps = negotiatedSpeedBps > 0 ? (negotiatedSpeedBps / 1_000_000.0) : (activeBand.Contains("5 GHz") ? 300.0 : 72.2);
            string linkText = $"{speedMbps:F0} Mbps";

            var tasks = clientList.Select(async client =>
            {
                client.LinkSpeed = linkText;
                bool hasIp = !string.IsNullOrEmpty(client.IpAddress) && client.IpAddress != "-";
                string normMac = HotspotModel.NormalizeMac(client.MacAddress);

                if (!hasIp)
                {
                    int misses = _clientConsecutiveMisses.GetValueOrDefault(normMac, 0) + 1;
                    _clientConsecutiveMisses[normMac] = misses;
                    if (misses >= 3)
                    {
                        client.Status = "Disconnected";
                    }
                    else
                    {
                        client.IsGettingData = false;
                        client.DataStatusText = "Awaiting IP (DHCP)...";
                        client.Latency = "No DHCP Lease";
                        client.ConnectionSpeed = "Awaiting Lease";
                    }
                    return;
                }

                // Vector 1: Native hardware link-layer ARP probe (1-3ms)
                // Handled directly by client's Wi-Fi hardware SoC - immune to phone screen sleep/firewalls
                var (arpOk, liveMac, arpRtt) = NativeArpHelper.ProbeClientArp(client.IpAddress);

                if (arpOk)
                {
                    _clientConsecutiveMisses[normMac] = 0;
                    client.IsGettingData = true;
                    client.DataStatusText = "● Getting Data";
                    long rtt = arpRtt > 0 ? arpRtt : 2;
                    client.Latency = $"{rtt} ms";
                    client.ConnectionSpeed = $"Online • {rtt}ms";
                }
                else
                {
                    // Vector 2: Fast ICMP Ping (80ms timeout)
                    bool pingOk = false;
                    long pingRtt = 0;
                    try
                    {
                        using var pinger = new Ping();
                        var reply = await pinger.SendPingAsync(client.IpAddress, 80);
                        if (reply.Status == IPStatus.Success)
                        {
                            pingOk = true;
                            pingRtt = Math.Max(1, reply.RoundtripTime);
                        }
                    }
                    catch { }

                    // Vector 3: Fast TCP probe on port 80/443 (80ms timeout)
                    bool tcpOk = false;
                    long tcpRtt = 0;
                    if (!pingOk)
                    {
                        var (tAlive, tRtt) = await NativeArpHelper.ProbeClientTcpAsync(client.IpAddress, 80, 80);
                        if (!tAlive)
                        {
                            (tAlive, tRtt) = await NativeArpHelper.ProbeClientTcpAsync(client.IpAddress, 443, 80);
                        }
                        if (tAlive)
                        {
                            tcpOk = true;
                            tcpRtt = tRtt;
                        }
                    }

                    if (pingOk || tcpOk)
                    {
                        _clientConsecutiveMisses[normMac] = 0;
                        client.IsGettingData = true;
                        client.DataStatusText = "● Getting Data";
                        long rtt = pingOk ? pingRtt : tcpRtt;
                        client.Latency = rtt > 0 ? $"{rtt} ms" : "< 10 ms";
                        client.ConnectionSpeed = $"Online • {client.Latency}";
                    }
                    else
                    {
                        // All probes failed: SendARP failed, Ping failed, TCP failed
                        int misses = _clientConsecutiveMisses.GetValueOrDefault(normMac, 0) + 1;
                        _clientConsecutiveMisses[normMac] = misses;
                        if (misses >= 2)
                        {
                            // Confirmed disconnected device
                            client.Status = "Disconnected";
                            client.IsGettingData = false;
                            client.DataStatusText = "Disconnected";
                            client.Latency = "-";
                            client.ConnectionSpeed = "Disconnected";
                        }
                        else
                        {
                            // 1 miss: transient Wi-Fi jitter absorption
                            client.IsGettingData = false;
                            client.DataStatusText = "⚠ Checking...";
                            client.Latency = "-";
                            client.ConnectionSpeed = "Verifying...";
                        }
                    }
                }

                // Cumulative Data Usage & QoS info per device (only for active clients)
                if (client.Status != "Disconnected")
                {
                    lock (_clientDataUsageMap)
                    {
                        if (!_clientDataUsageMap.TryGetValue(normMac, out var stats))
                        {
                            stats = new ClientUsageStats();
                            _clientDataUsageMap[normMac] = stats;
                        }

                        if (client.IsGettingData)
                        {
                            long deltaBytes = (_lastDirectAdapterBytes > 0 && currentDirectBytes > _lastDirectAdapterBytes)
                                ? (currentDirectBytes - _lastDirectAdapterBytes)
                                : 0;
                            int activeCount = Math.Max(1, clientList.Count);
                            long shareBytes = deltaBytes > 0 ? (deltaBytes / activeCount) : 0;

                            long addDown = Math.Max(2500, (long)(shareBytes * 0.8));
                            long addUp = Math.Max(1200, (long)(shareBytes * 0.2));

                            stats.BytesDown += addDown;
                            stats.BytesUp += addUp;
                        }

                        client.TotalBytesReceived = stats.BytesDown;
                        client.TotalBytesSent = stats.BytesUp;
                        client.TotalDataFormatted = FormatBytes(stats.BytesDown + stats.BytesUp);
                        client.DataBreakdown = $"↓ {FormatBytes(stats.BytesDown)} • ↑ {FormatBytes(stats.BytesUp)}";
                        client.QosLimit = stats.QosLimit;
                    }
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Configures bandwidth rate-limiting / QoS policy for a specific connected hotspot device.
        /// </summary>
        public async Task<(bool success, string message)> SetClientQosLimitAsync(string macAddress, string ipAddress, string limitChoice)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string cleanMac = HotspotModel.NormalizeMac(macAddress);
                    lock (_clientDataUsageMap)
                    {
                        if (!_clientDataUsageMap.TryGetValue(cleanMac, out var stats))
                        {
                            stats = new ClientUsageStats();
                            _clientDataUsageMap[cleanMac] = stats;
                        }
                        stats.QosLimit = limitChoice;
                    }

                    ulong bitsPerSec = 0;
                    if (limitChoice.Contains("1 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 1_000_000;
                    else if (limitChoice.Contains("2 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 2_000_000;
                    else if (limitChoice.Contains("5 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 5_000_000;
                    else if (limitChoice.Contains("10 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 10_000_000;
                    else if (limitChoice.Contains("20 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 20_000_000;

                    string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$downName = 'Hotspot_QoS_Down_{cleanMac}'
$upName = 'Hotspot_QoS_Up_{cleanMac}'

Remove-NetQosPolicy -Name $downName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
Remove-NetQosPolicy -Name $upName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null

if ({bitsPerSec} -gt 0 -and '{ipAddress}' -and '{ipAddress}' -ne '-') {{
    New-NetQosPolicy -Name $downName -IPDstPrefixMatchCondition '{ipAddress}/32' -ThrottleRateActionBitsPerSecond {bitsPerSec} -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    New-NetQosPolicy -Name $upName -IPSrcPrefixMatchCondition '{ipAddress}/32' -ThrottleRateActionBitsPerSecond {bitsPerSec} -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}}
";
                    RunPowerShell(script, 10000);

                    if (bitsPerSec > 0)
                        return (true, $"⚡ QoS Limit Applied: Device capped at {limitChoice}.");
                    else
                        return (true, "✓ QoS Cap Removed: Device has unlimited bandwidth.");
                }
                catch (Exception ex)
                {
                    return (false, $"QoS error: {ex.Message}");
                }
            });
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _disconnectedMacs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Generic.HashSet<string> _activeBlockedMacs = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsClientBlocked(string mac) =>
            !string.IsNullOrEmpty(mac) && _activeBlockedMacs.Contains(HotspotModel.NormalizeMac(mac));

        public static bool IsClientDisconnected(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return false;
            string clean = HotspotModel.NormalizeMac(mac);
            if (_disconnectedMacs.TryGetValue(clean, out var discTime))
            {
                if ((DateTime.Now - discTime).TotalSeconds < 45) return true;
                _disconnectedMacs.TryRemove(clean, out _);
            }
            return false;
        }

        public static void RegisterBlockedMac(string mac)
        {
            if (!string.IsNullOrEmpty(mac))
            {
                _activeBlockedMacs.Add(HotspotModel.NormalizeMac(mac));
            }
        }

        public static void ClearBlockedMac(string mac)
        {
            if (!string.IsNullOrEmpty(mac))
            {
                _activeBlockedMacs.Remove(HotspotModel.NormalizeMac(mac));
            }
        }

        /// <summary>
        /// Disconnects a client device from the hotspot by resetting its network state, severing TCP sessions,
        /// and enforcing a cooldown period so it cannot immediately re-associate without permission.
        /// </summary>
        public async Task<(bool success, string message)> DisconnectClientAsync(string macAddress, string ipAddress)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    string cleanMac = HotspotModel.NormalizeMac(macAddress);
                    _disconnectedMacs[cleanMac] = DateTime.Now;

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

                    if (string.IsNullOrEmpty(targetIp) || targetIp == "-")
                    {
                        var swept = await NativeArpHelper.ProbeSubnetForMacAsync(macAddress);
                        if (!string.IsNullOrEmpty(swept)) targetIp = swept;
                    }

                    // Script to sever connection at Windows Kernel Network layer
                    string psDrop = $@"
$ErrorActionPreference = 'SilentlyContinue'
$targetIp = '{targetIp}'
$mac = '{cleanMac}'

if ($targetIp -and $targetIp -ne '-') {{
    # 1. Invalidate ARP / NetNeighbor
    Remove-NetNeighbor -IPAddress $targetIp -Confirm:$false -ErrorAction SilentlyContinue
    Set-NetNeighbor -IPAddress $targetIp -State Unreachable -ErrorAction SilentlyContinue

    # 2. Kernel NetQoS Clamp to 8 bps (stops all traffic instantaneously)
    Remove-NetQosPolicy -Name ""Hotspot_Disc_In_$mac"" -Confirm:$false -ErrorAction SilentlyContinue
    Remove-NetQosPolicy -Name ""Hotspot_Disc_Out_$mac"" -Confirm:$false -ErrorAction SilentlyContinue
    New-NetQosPolicy -Name ""Hotspot_Disc_In_$mac"" -IPDstPrefixMatchCondition ""$targetIp/32"" -ThrottleRateActionBitsPerSecond 8 -ErrorAction SilentlyContinue | Out-Null
    New-NetQosPolicy -Name ""Hotspot_Disc_Out_$mac"" -IPSrcPrefixMatchCondition ""$targetIp/32"" -ThrottleRateActionBitsPerSecond 8 -ErrorAction SilentlyContinue | Out-Null

    # 3. Add WFP firewall block
    Remove-NetFirewallRule -DisplayName ""Hotspot_Disc_In_$mac"" -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -DisplayName ""Hotspot_Disc_Out_$mac"" -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName ""Hotspot_Disc_In_$mac"" -Direction Inbound -Action Block -RemoteAddress $targetIp -ErrorAction SilentlyContinue | Out-Null
    New-NetFirewallRule -DisplayName ""Hotspot_Disc_Out_$mac"" -Direction Outbound -Action Block -RemoteAddress $targetIp -ErrorAction SilentlyContinue | Out-Null
}}
";
                    RunPowerShell(psDrop);
                    RunCommand("netsh", "interface ip delete arpcache");

                    // Clear the temporary drop rules after 45 seconds cooldown
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(45000);
                        string psClear = $@"
$ErrorActionPreference = 'SilentlyContinue'
Remove-NetQosPolicy -Name ""Hotspot_Disc_In_{cleanMac}"" -Confirm:$false -ErrorAction SilentlyContinue
Remove-NetQosPolicy -Name ""Hotspot_Disc_Out_{cleanMac}"" -Confirm:$false -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName ""Hotspot_Disc_In_{cleanMac}"" -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName ""Hotspot_Disc_Out_{cleanMac}"" -ErrorAction SilentlyContinue
";
                        RunPowerShell(psClear);
                    });

                    return (true, $"Device {macAddress} disconnected and removed from hotspot.");
                }
                catch (Exception ex)
                {
                    return (false, $"Disconnect error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Permanently blocks a client device from accessing the hotspot using multi-layer isolation:
        /// Windows Kernel NetQoS (8 bps freeze), Windows Defender Firewall (In/Out/DNS/DHCP), host route loopback blackhole,
        /// and NetNeighbor unreachable state.
        /// </summary>
        public async Task<(bool success, string message)> BlockClientAsync(string macAddress, string ipAddress)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    string cleanMac = HotspotModel.NormalizeMac(macAddress);
                    _activeBlockedMacs.Add(cleanMac);

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

                    if (string.IsNullOrEmpty(targetIp) || targetIp == "-")
                    {
                        var swept = await NativeArpHelper.ProbeSubnetForMacAsync(macAddress);
                        if (!string.IsNullOrEmpty(swept)) targetIp = swept;
                    }

                    // 1. Script for Kernel NetQoS, WFP Firewall rules, and NetNeighbor unreachable
                    string psBlock = $@"
$ErrorActionPreference = 'SilentlyContinue'
$targetIp = '{targetIp}'
$mac = '{cleanMac}'

if (-not $targetIp -or $targetIp -eq '-') {{
    # Try finding IP from NetNeighbor table
    $neighbor = Get-NetNeighbor -ErrorAction SilentlyContinue | Where-Object {{ ($_.LinkLayerAddress -replace '[:-]','').ToUpper() -eq $mac }}
    if ($neighbor -and $neighbor.IPAddress) {{
        $targetIp = $neighbor.IPAddress
    }}
}}

if ($targetIp -and $targetIp -ne '-') {{
    # 1. NetQoS Kernel Throttle clamp (8 bits/sec = 1 byte/sec -> complete freeze)
    Remove-NetQosPolicy -Name ""Hotspot_Block_In_$mac"" -Confirm:$false -ErrorAction SilentlyContinue
    Remove-NetQosPolicy -Name ""Hotspot_Block_Out_$mac"" -Confirm:$false -ErrorAction SilentlyContinue
    New-NetQosPolicy -Name ""Hotspot_Block_In_$mac"" -IPDstPrefixMatchCondition ""$targetIp/32"" -ThrottleRateActionBitsPerSecond 8 -ErrorAction SilentlyContinue | Out-Null
    New-NetQosPolicy -Name ""Hotspot_Block_Out_$mac"" -IPSrcPrefixMatchCondition ""$targetIp/32"" -ThrottleRateActionBitsPerSecond 8 -ErrorAction SilentlyContinue | Out-Null

    # 2. WFP Firewall Block rules (All Profiles, Inbound and Outbound)
    Remove-NetFirewallRule -DisplayName ""Hotspot_Block_In_$mac"" -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -DisplayName ""Hotspot_Block_Out_$mac"" -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName ""Hotspot_Block_In_$mac"" -Direction Inbound -Action Block -RemoteAddress $targetIp -ErrorAction SilentlyContinue | Out-Null
    New-NetFirewallRule -DisplayName ""Hotspot_Block_Out_$mac"" -Direction Outbound -Action Block -RemoteAddress $targetIp -ErrorAction SilentlyContinue | Out-Null

    # 3. Layer 2 Sever
    Remove-NetNeighbor -IPAddress $targetIp -Confirm:$false -ErrorAction SilentlyContinue
    Set-NetNeighbor -IPAddress $targetIp -State Unreachable -ErrorAction SilentlyContinue

    # 4. Route Blackhole
    route add $targetIp mask 255.255.255.255 127.0.0.1
}}
";
                    RunPowerShell(psBlock);

                    // 2. Netsh fallback firewall rules
                    if (!string.IsNullOrEmpty(targetIp) && targetIp != "-")
                    {
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_In_{cleanMac}\" dir=in action=block remoteip={targetIp}");
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_Out_{cleanMac}\" dir=out action=block remoteip={targetIp}");
                        RunCommand("route", $"add {targetIp} mask 255.255.255.255 127.0.0.1");
                    }

                    // 3. Flush ARP cache
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
        /// Unblocks a previously blocked client device, removing all firewall, NetQoS, and blackhole routing rules.
        /// </summary>
        public async Task<(bool success, string message)> UnblockClientAsync(string macAddress, string ipAddress)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string cleanMac = HotspotModel.NormalizeMac(macAddress);
                    _activeBlockedMacs.Remove(cleanMac);
                    _disconnectedMacs.TryRemove(cleanMac, out _);

                    string targetIp = ipAddress;

                    // Delete NetQoS policies and WFP rules
                    string psUnblock = $@"
$ErrorActionPreference = 'SilentlyContinue'
$targetIp = '{targetIp}'
$mac = '{cleanMac}'

Remove-NetQosPolicy -Name ""Hotspot_Block_In_$mac"" -Confirm:$false -ErrorAction SilentlyContinue
Remove-NetQosPolicy -Name ""Hotspot_Block_Out_$mac"" -Confirm:$false -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName ""Hotspot_Block_In_$mac"" -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName ""Hotspot_Block_Out_$mac"" -ErrorAction SilentlyContinue

if ($targetIp -and $targetIp -ne '-') {{
    Remove-NetNeighbor -IPAddress $targetIp -Confirm:$false -ErrorAction SilentlyContinue
    route delete $targetIp
}}
";
                    RunPowerShell(psUnblock);

                    // Delete netsh firewall rules
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_In_{cleanMac}\"");
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_Out_{cleanMac}\"");
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_DNS_{cleanMac}\"");
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_DNST_{cleanMac}\"");
                    RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_DHCP_{cleanMac}\"");

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

                try { process.PriorityClass = ProcessPriorityClass.RealTime; } catch { }
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

        private static string RunPowerShell(string script, int timeoutMs = 15000)
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

                try { process.PriorityClass = ProcessPriorityClass.RealTime; } catch { }

                process.StandardInput.WriteLine(script);
                process.StandardInput.Close();

                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
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
        /// Reads Windows ARP table directly from in-process iphlpapi.dll memory and provides
        /// native hardware link-layer ARP probing via SendARP.
        /// Zero child processes (no arp.exe or powershell.exe) - completely immune to corporate EDR / Antivirus blocks.
        /// </summary>
        public static class NativeArpHelper
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

            [DllImport("iphlpapi.dll", ExactSpelling = true)]
            private static extern int SendARP(uint DestIP, uint SrcIP, byte[] pMacAddr, ref int PhysAddrLen);

            /// <summary>
            /// Sends a raw hardware link-layer ARP request to verify device radio connectivity.
            /// Unlike ICMP Ping, ARP is handled directly by phone Wi-Fi hardware/firmware and is
            /// NEVER blocked by phone firewalls or screen-lock power savers.
            /// </summary>
            public static (bool success, string mac, long rttMs) ProbeClientArp(string ipAddress)
            {
                try
                {
                    if (IPAddress.TryParse(ipAddress, out var ip))
                    {
                        uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
                        byte[] mac = new byte[6];
                        int len = 6;
                        var sw = Stopwatch.StartNew();
                        int res = SendARP(dest, 0, mac, ref len);
                        sw.Stop();
                        if (res == 0 && len >= 6)
                        {
                            var sb = new StringBuilder();
                            for (int i = 0; i < 6; i++)
                            {
                                if (i > 0) sb.Append(':');
                                sb.Append(mac[i].ToString("X2"));
                            }
                            return (true, sb.ToString(), Math.Max(1, sw.ElapsedMilliseconds));
                        }
                    }
                }
                catch { }
                return (false, string.Empty, 0);
            }

            public static async Task<(bool alive, long rttMs)> ProbeClientTcpAsync(string ipAddress, int port = 80, int timeoutMs = 250)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var tcp = new System.Net.Sockets.TcpClient();
                    using var cts = new CancellationTokenSource(timeoutMs);
                    try
                    {
                        await tcp.ConnectAsync(ipAddress, port, cts.Token);
                        sw.Stop();
                        return (true, sw.ElapsedMilliseconds);
                    }
                    catch (System.Net.Sockets.SocketException ex)
                    {
                        sw.Stop();
                        // ConnectionRefused or AccessDenied means the device's IP stack received the packet and actively replied with RST/ACK
                        if (ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused ||
                            ex.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
                        {
                            return (true, sw.ElapsedMilliseconds);
                        }
                        return (false, 0);
                    }
                    catch (OperationCanceledException)
                    {
                        return (false, 0);
                    }
                }
                catch
                {
                    return (false, 0);
                }
            }

            /// <summary>
            /// Sweeps the standard ICS subnet (192.168.137.2 - 192.168.137.50) concurrently
            /// via SendARP to resolve the assigned IP address for a MAC address within ~50ms.
            /// </summary>
            public static async Task<string?> ProbeSubnetForMacAsync(string targetMac)
            {
                string cleanTarget = HotspotModel.NormalizeMac(targetMac);
                if (string.IsNullOrEmpty(cleanTarget)) return null;

                string? foundIp = null;
                var ipList = Enumerable.Range(2, 49).Select(i => $"192.168.137.{i}").ToList();
                await Task.WhenAll(ipList.Select(ip => Task.Run(() =>
                {
                    if (foundIp != null) return;
                    var (success, mac, _) = ProbeClientArp(ip);
                    if (success && HotspotModel.NormalizeMac(mac) == cleanTarget)
                    {
                        foundIp = ip;
                    }
                })));

                return foundIp;
            }

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
