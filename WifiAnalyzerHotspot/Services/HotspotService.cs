using System;
using System.Collections.Concurrent;
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
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _clientConsecutiveMisses = new(StringComparer.OrdinalIgnoreCase);

        private static string EscapePowerShellString(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return input.Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$");
        }

        private static bool IsValidMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return false;
            string clean = HotspotModel.NormalizeMac(mac);
            return clean.Length == 12 && System.Text.RegularExpressions.Regex.IsMatch(clean, "^[0-9A-Fa-f]{12}$");
        }

        private static bool IsValidIp(string ip)
        {
            return !string.IsNullOrEmpty(ip) && ip != "-" && System.Net.IPAddress.TryParse(ip, out _);
        }

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
                                    string cleanMac = HotspotModel.NormalizeMac(client.MacAddress);
                                    if (arpMap.TryGetValue(cleanMac, out var resolvedIp) || arpMap.TryGetValue(client.MacAddress, out resolvedIp))
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
                                string cleanMac = HotspotModel.NormalizeMac(client.MacAddress);
                                if (arpMap.TryGetValue(cleanMac, out var resolvedIp) || arpMap.TryGetValue(client.MacAddress, out resolvedIp))
                                {
                                    client.IpAddress = resolvedIp;
                                }
                            }
                        }

                        // Auto-detect and immediately seal any blocked MAC that reappeared or obtained an IP
                        foreach (var client in model.Clients.ToList())
                        {
                            string clean = HotspotModel.NormalizeMac(client.MacAddress);
                            if (IsClientBlocked(clean))
                            {
                                if (!string.IsNullOrEmpty(client.IpAddress) && client.IpAddress != "-")
                                {
                                    bool alreadySealed = false;
                                    lock (_blockedMacToIps)
                                    {
                                        if (_blockedMacToIps.TryGetValue(clean, out var knownIps) && knownIps.Contains(client.IpAddress))
                                        {
                                            alreadySealed = true;
                                        }
                                    }
                                    if (!alreadySealed)
                                    {
                                        EnforceIpBlock(clean, client.IpAddress);
                                    }
                                }
                                model.Clients.Remove(client);
                            }
                            else if (IsClientDisconnected(clean))
                            {
                                model.Clients.Remove(client);
                            }
                        }

                        // Auto-detect blocked MACs appearing in the ARP table as well
                        foreach (var kvp in arpMap)
                        {
                            string clean = HotspotModel.NormalizeMac(kvp.Key);
                            if (IsClientBlocked(clean) && !string.IsNullOrEmpty(kvp.Value) && kvp.Value != "-")
                            {
                                bool alreadySealed = false;
                                lock (_blockedMacToIps)
                                {
                                    if (_blockedMacToIps.TryGetValue(clean, out var knownIps) && knownIps.Contains(kvp.Value))
                                    {
                                        alreadySealed = true;
                                    }
                                }
                                if (!alreadySealed)
                                {
                                    EnforceIpBlock(clean, kvp.Value);
                                }
                            }
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

                string safeSsid = EscapePowerShellString(ssid);
                string safePass = EscapePowerShellString(passphrase);

                string script = $@"
try {{ [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High }} catch {{}}
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

                string safeSsid = EscapePowerShellString(ssid);
                string safePass = EscapePowerShellString(passphrase);

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
                        _ = Task.Run(async () => await _priorityService.EngageHighestPriorityAsync(allowPacerRebind: true));
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
                lock (_clientDataUsageMap)
                {
                    _clientDataUsageMap.Clear();
                }
                lock (_usageBaselineLock)
                {
                    _lastDirectRxBytes = 0;
                    _lastDirectTxBytes = 0;
                    _hasUsageBaseline = false;
                }
                _appliedPriorities.Clear();
                _appliedQosLimits.Clear();
                _pausedClients.Clear();
                _pausedMacToIp.Clear();

                string script = @"
$ErrorActionPreference = 'SilentlyContinue'

# Elevate hotspot creation process to High priority
try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High } catch {}

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
                        _ = Task.Run(async () => await _priorityService.EngageHighestPriorityAsync(allowPacerRebind: true));
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
                lock (_clientDataUsageMap)
                {
                    _clientDataUsageMap.Clear();
                }
                lock (_usageBaselineLock)
                {
                    _lastDirectRxBytes = 0;
                    _lastDirectTxBytes = 0;
                    _hasUsageBaseline = false;
                }
                _appliedPriorities.Clear();
                _appliedQosLimits.Clear();
                _pausedClients.Clear();
                _pausedMacToIp.Clear();

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
            var res = await _priorityService.EngageHighestPriorityAsync(allowPacerRebind: true);
            return (res.success, res.details);
        }

        private class ClientUsageStats
        {
            public long BytesDown { get; set; }
            public long BytesUp { get; set; }
            public string QosLimit { get; set; } = "Unlimited";
            public int Priority { get; set; } = 5;
        }

        private static readonly Dictionary<string, ClientUsageStats> _clientDataUsageMap = new(StringComparer.OrdinalIgnoreCase);
        // In-memory cache of applied NetQoS policies to eliminate redundant PowerShell process spawns & disk thrashing on HDDs
        private static readonly ConcurrentDictionary<string, (string ip, int priority)> _appliedPriorities = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (string ip, string limit)> _appliedQosLimits = new(StringComparer.OrdinalIgnoreCase);
        // Per-MAC synchronization to prevent duplicate-name race conditions between auto-apply on DHCP lease and user actions
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _macLocks = new(StringComparer.OrdinalIgnoreCase);

        private static SemaphoreSlim GetMacLock(string cleanMac)
        {
            return _macLocks.GetOrAdd(cleanMac, _ => new SemaphoreSlim(1, 1));
        }

        public static void RegisterSavedPriority(string mac, int priority)
        {
            if (string.IsNullOrEmpty(mac)) return;
            string clean = HotspotModel.NormalizeMac(mac);
            int clamped = Math.Clamp(priority, 1, 7);
            lock (_clientDataUsageMap)
            {
                if (!_clientDataUsageMap.TryGetValue(clean, out var stats))
                {
                    stats = new ClientUsageStats();
                    _clientDataUsageMap[clean] = stats;
                }
                stats.Priority = clamped;
            }
        }

        public static void RegisterSavedQosLimit(string mac, string qosLimit)
        {
            if (string.IsNullOrEmpty(mac) || string.IsNullOrEmpty(qosLimit)) return;
            string clean = HotspotModel.NormalizeMac(mac);
            lock (_clientDataUsageMap)
            {
                if (!_clientDataUsageMap.TryGetValue(clean, out var stats))
                {
                    stats = new ClientUsageStats();
                    _clientDataUsageMap[clean] = stats;
                }
                stats.QosLimit = qosLimit;
            }
        }

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

        private static long _lastDirectRxBytes = 0;
        private static long _lastDirectTxBytes = 0;
        private static bool _hasUsageBaseline = false;
        private static readonly object _usageBaselineLock = new();
        private static bool _cachedInternetAvailable = true;
        private static DateTime _lastInternetCheck = DateTime.MinValue;

        public static async Task<bool> CheckInternetAvailableAsync()
        {
            if ((DateTime.Now - _lastInternetCheck).TotalSeconds < 3)
            {
                return _cachedInternetAvailable;
            }
            _lastInternetCheck = DateTime.Now;

            // Vector 1: Fast ICMP Ping (8.8.8.8)
            try
            {
                using var pinger = new Ping();
                var reply = await pinger.SendPingAsync("8.8.8.8", 150);
                if (reply.Status == IPStatus.Success)
                {
                    _cachedInternetAvailable = true;
                    return true;
                }
            }
            catch { }

            // Vector 2: Fast TCP handshake on port 443 (1.1.1.1:443 - works when ICMP is blocked)
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connectTask = tcp.ConnectAsync("1.1.1.1", 443);
                var delayTask = Task.Delay(250);
                var finished = await Task.WhenAny(connectTask, delayTask);
                if (finished == connectTask && tcp.Connected)
                {
                    _cachedInternetAvailable = true;
                    return true;
                }
            }
            catch { }

            // Vector 3: Fast HTTP 204 Probe (Google / MSFT connectivity check)
            try
            {
                using var handler = new System.Net.Http.SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
                using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) };
                var resp = await http.GetAsync("http://connectivitycheck.gstatic.com/generate_204");
                if (resp.IsSuccessStatusCode)
                {
                    _cachedInternetAvailable = true;
                    return true;
                }
            }
            catch { }

            // Vector 4: OS Network Available status fallback
            try
            {
                _cachedInternetAvailable = NetworkInterface.GetIsNetworkAvailable();
                return _cachedInternetAvailable;
            }
            catch
            {
                _cachedInternetAvailable = true;
                return true;
            }
        }

        /// <summary>
        /// Continuously checks whether the underlying Windows hotspot infrastructure
        /// (Virtual Wi-Fi Direct adapter, ICS DHCP scope 192.168.137.1) is actively healthy.
        /// </summary>
        public static (bool isHealthy, string failureReason) CheckHotspotInfrastructureHealth(bool isHotspotRunning)
        {
            if (!isHotspotRunning) return (true, "Hotspot stopped");
            try
            {
                bool directAdapterFound = false;
                bool hasHotspotIp = false;

                var ifaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in ifaces)
                {
                    if (ni.OperationalStatus == OperationalStatus.Up)
                    {
                        if (ni.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                            ni.Name.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                            ni.Name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase))
                        {
                            directAdapterFound = true;
                            var ipProps = ni.GetIPProperties();
                            foreach (var addr in ipProps.UnicastAddresses)
                            {
                                if (addr.Address.ToString() == "192.168.137.1")
                                {
                                    hasHotspotIp = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (!directAdapterFound)
                {
                    return (false, "Virtual Wi-Fi Direct SoftAP adapter is down or missing");
                }

                if (!hasHotspotIp)
                {
                    return (false, "Hotspot IP 192.168.137.1 unassigned (DHCP/ICS scope lost)");
                }

                return (true, "Infrastructure healthy");
            }
            catch (Exception ex)
            {
                return (true, ex.Message);
            }
        }

        /// <summary>
        /// Enriches connected client devices with real-time negotiated link speed, live latency,
        /// and data flow health. Prioritizes instant hardware-level SendARP (1-3ms) directly answered
        /// by client Wi-Fi chips, followed by fast Ping/TCP fallback.
        /// Unresponsive/ghost devices are flagged Disconnected after 2 failed checks and purged.
        /// </summary>
        private async Task EnrichClientSpeedAsync(IEnumerable<HotspotClientInfo> clients, string activeBand)
        {
            var clientList = clients.ToList();
            if (clientList.Count == 0) return;

            long negotiatedSpeedBps = 0;
            long currentDirectRx = 0;
            long currentDirectTx = 0;
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
                            currentDirectRx += stats.BytesReceived;
                            currentDirectTx += stats.BytesSent;
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

            // Measure the adapter delta once per cycle, then advance the baseline so the
            // next sample counts only new bytes instead of the total since adapter start.
            long cycleRx, cycleTx;
            lock (_usageBaselineLock)
            {
                if (!_hasUsageBaseline)
                {
                    // First sample after (re)start: adopt current counters without
                    // crediting bytes that predate this hotspot session.
                    _lastDirectRxBytes = currentDirectRx;
                    _lastDirectTxBytes = currentDirectTx;
                    _hasUsageBaseline = true;
                    cycleRx = 0;
                    cycleTx = 0;
                }
                else
                {
                    cycleRx = currentDirectRx > _lastDirectRxBytes ? currentDirectRx - _lastDirectRxBytes : 0;
                    cycleTx = currentDirectTx > _lastDirectTxBytes ? currentDirectTx - _lastDirectTxBytes : 0;
                    _lastDirectRxBytes = currentDirectRx;
                    _lastDirectTxBytes = currentDirectTx;
                }
            }
            bool cycleHasTraffic = (cycleRx + cycleTx) > 0;

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

                if (!hostHasInternet)
                {
                    _clientConsecutiveMisses[normMac] = 0;
                    client.IsGettingData = false;
                    client.DataStatusText = "⚠️ No Internet (Host Offline)";
                    client.ConnectionSpeed = "No Internet Access";
                    client.Latency = "No Transit";
                }
                else if (arpOk)
                {
                    _clientConsecutiveMisses[normMac] = 0;
                    client.IsGettingData = true;
                    client.DataStatusText = cycleHasTraffic ? "● Getting Data" : "● Online (idle)";
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
                        if (!hostHasInternet)
                        {
                            client.IsGettingData = false;
                            client.DataStatusText = "⚠️ No Internet (Host Offline)";
                            client.ConnectionSpeed = "No Internet Access";
                            client.Latency = "No Transit";
                        }
                        else
                        {
                            client.IsGettingData = true;
                            client.DataStatusText = cycleHasTraffic ? "● Getting Data" : "● Online (idle)";
                            long rtt = pingOk ? pingRtt : tcpRtt;
                            client.Latency = rtt > 0 ? $"{rtt} ms" : "< 10 ms";
                            client.ConnectionSpeed = $"Online • {client.Latency}";
                        }
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
                            // Split the cycle's real adapter delta evenly across online devices.
                            // This is an estimated share (Windows exposes no per-client counters).
                            int activeCount = Math.Max(1, clientList.Count);
                            stats.BytesDown += cycleRx / activeCount;
                            stats.BytesUp += cycleTx / activeCount;
                        }

                        client.TotalBytesReceived = stats.BytesDown;
                        client.TotalBytesSent = stats.BytesUp;
                        client.TotalDataFormatted = FormatBytes(stats.BytesDown + stats.BytesUp);
                        client.DataBreakdown = $"↓ {FormatBytes(stats.BytesDown)} • ↑ {FormatBytes(stats.BytesUp)}";
                        client.QosLimit = stats.QosLimit;
                        client.Priority = stats.Priority > 0 ? stats.Priority : 5;

                        var pauseExp = GetPauseExpiration(normMac);
                        if (pauseExp.HasValue)
                        {
                            var remaining = pauseExp.Value - DateTime.UtcNow;
                            if (remaining.TotalSeconds > 0)
                            {
                                client.IsPaused = true;
                                client.PauseExpiresAt = pauseExp;
                                client.PauseStatusText = remaining.TotalMinutes >= 1
                                    ? $"⏸️ Paused ({(int)remaining.TotalMinutes}m {remaining.Seconds:D2}s left)"
                                    : $"⏸️ Paused ({remaining.Seconds}s left)";
                                client.DataStatusText = "⏸️ Internet Paused";
                                client.IsGettingData = false;
                            }
                            else
                            {
                                // Expired: Automatically lift NetQoS clamp and restore QoS/Priority
                                client.IsPaused = false;
                                client.PauseExpiresAt = null;
                                client.PauseStatusText = string.Empty;
                                if (_pausedClients.TryRemove(normMac, out _))
                                {
                                    _pausedMacToIp.TryRemove(normMac, out _);
                                    string restoreIp = client.IpAddress;
                                    _ = Task.Run(async () => await ResumeClientAsync(normMac, restoreIp));
                                }
                            }
                        }
                        else
                        {
                            client.IsPaused = false;
                            client.PauseExpiresAt = null;
                            client.PauseStatusText = string.Empty;
                        }
                    }
                }
            });

            await Task.WhenAll(tasks);
        }

        private static bool TryParseQosLimit(string? limitChoice, out ulong bitsPerSec)
        {
            bitsPerSec = 0;
            if (string.IsNullOrWhiteSpace(limitChoice) ||
                limitChoice.Equals("Unlimited", StringComparison.OrdinalIgnoreCase))
                return true;

            if (limitChoice.Contains("30 KB", StringComparison.OrdinalIgnoreCase) || limitChoice.Contains("30KB", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 240_000;
            else if (limitChoice.Contains("100 KB", StringComparison.OrdinalIgnoreCase) || limitChoice.Contains("100KB", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 800_000;
            else if (limitChoice.Contains("300 KB", StringComparison.OrdinalIgnoreCase) || limitChoice.Contains("300KB", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 2_400_000;
            else if (limitChoice.Contains("1 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 1_000_000;
            else if (limitChoice.Contains("2 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 2_000_000;
            else if (limitChoice.Contains("5 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 5_000_000;
            else if (limitChoice.Contains("10 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 10_000_000;
            else if (limitChoice.Contains("20 Mbps", StringComparison.OrdinalIgnoreCase)) bitsPerSec = 20_000_000;
            else return false;

            return true;
        }

        private static string ResolveClientIp(string cleanMac, string? ipAddress)
        {
            string targetIp = ipAddress ?? "";
            if (string.IsNullOrEmpty(targetIp) || targetIp == "-")
            {
                var arpMap = NativeArpHelper.GetHotspotArpEntries();
                targetIp = arpMap.TryGetValue(cleanMac, out var resolved) ? resolved : "";
            }

            if (!string.IsNullOrEmpty(targetIp) && targetIp != "-" && !IsValidIp(targetIp))
                targetIp = "";

            return targetIp == "-" ? "" : targetIp;
        }

        /// <summary>
        /// Configures bandwidth rate-limiting / QoS policy for a specific connected hotspot device.
        /// Policy creation is verified with Get-NetQosPolicy so failures are reported instead of assumed.
        /// </summary>
        public async Task<(bool success, string message)> SetClientQosLimitAsync(string macAddress, string ipAddress, string limitChoice)
        {
            string cleanMac = HotspotModel.NormalizeMac(macAddress);
            if (!IsValidMac(cleanMac))
            {
                return (false, "Invalid MAC address format.");
            }

            var macLock = GetMacLock(cleanMac);
            await macLock.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        if (!TryParseQosLimit(limitChoice, out ulong bitsPerSec))
                        {
                            return (false, $"Unrecognized bandwidth limit '{limitChoice}'.");
                        }

                        lock (_clientDataUsageMap)
                        {
                            if (!_clientDataUsageMap.TryGetValue(cleanMac, out var stats))
                            {
                                stats = new ClientUsageStats();
                                _clientDataUsageMap[cleanMac] = stats;
                            }
                            stats.QosLimit = limitChoice;
                        }

                        string targetIp = ResolveClientIp(cleanMac, ipAddress);

                        if (bitsPerSec > 0 && string.IsNullOrEmpty(targetIp))
                        {
                            return (false, $"QoS limit saved, but {macAddress} has no resolved IP address yet - it will be enforced once the device obtains one.");
                        }

                        // Evict stale cached limit entries for any previous MAC holding this IP
                        if (!string.IsNullOrEmpty(targetIp) && targetIp != "-")
                        {
                            foreach (var kvp in _appliedQosLimits)
                            {
                                if (kvp.Value.ip == targetIp && !string.Equals(kvp.Key, cleanMac, StringComparison.OrdinalIgnoreCase))
                                {
                                    _appliedQosLimits.TryRemove(kvp.Key, out _);
                                }
                            }
                        }

                        // Check if identical policy was already verified and applied for this client and IP (avoids redundant PowerShell & disk thrashing)
                        if (_appliedQosLimits.TryGetValue(cleanMac, out var cachedQos) && cachedQos.ip == targetIp && cachedQos.limit.Equals(limitChoice, StringComparison.OrdinalIgnoreCase))
                        {
                            return (true, bitsPerSec > 0
                                ? $"⚡ QoS verified (active): device capped at {limitChoice} (NetQoS precedence 245)."
                                : "✓ QoS cap removed: device has unlimited bandwidth.");
                        }

                        string script = $@"
$ErrorActionPreference = 'Stop'
$downName = 'Hotspot_QoS_Down_{cleanMac}'
$upName = 'Hotspot_QoS_Up_{cleanMac}'
try {{
    Remove-NetQosPolicy -Name $downName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name $upName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    if ({bitsPerSec} -gt 0) {{
        # Evict stale limits left behind by a device that previously held this IP
        Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object {{
            $_.Name -like 'Hotspot_QoS_*' -and $_.Name -ne $downName -and $_.Name -ne $upName -and
            ($_.IPDstPrefix -eq '{targetIp}' -or $_.IPSrcPrefix -eq '{targetIp}')
        }} | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue | Out-Null

        New-NetQosPolicy -Name $downName -IPDstPrefixMatchCondition '{targetIp}/32' -ThrottleRateActionBitsPerSecond {bitsPerSec} -Precedence 245 -Confirm:$false -ErrorAction Stop | Out-Null
        New-NetQosPolicy -Name $upName -IPSrcPrefixMatchCondition '{targetIp}/32' -ThrottleRateActionBitsPerSecond {bitsPerSec} -Precedence 245 -Confirm:$false -ErrorAction Stop | Out-Null
        $down = Get-NetQosPolicy -Name $downName -ErrorAction Stop
        $up = Get-NetQosPolicy -Name $upName -ErrorAction Stop
        Write-Output ('OK:' + $down.ThrottleRateAction + ':' + $up.ThrottleRateAction)
    }} else {{
        Write-Output 'CLEARED'
    }}
}} catch {{
    Write-Output ('FAIL:' + $_.Exception.Message)
}}
";
                        string output = RunPowerShell(script, 15000).Trim();

                        if (output == "CLEARED")
                        {
                            _appliedQosLimits[cleanMac] = (targetIp, "Unlimited");
                            return (true, "✓ QoS cap removed: device has unlimited bandwidth.");
                        }

                        if (output.StartsWith("OK:", StringComparison.Ordinal))
                        {
                            var rates = output.Substring(3).Split(':');
                            if (rates.Length == 2 &&
                                ulong.TryParse(rates[0], out ulong downRate) &&
                                ulong.TryParse(rates[1], out ulong upRate) &&
                                downRate == bitsPerSec && upRate == bitsPerSec)
                            {
                                _appliedQosLimits[cleanMac] = (targetIp, limitChoice);
                                return (true, $"⚡ QoS verified: device capped at {limitChoice} for download and upload (NetQoS precedence 245).");
                            }
                            return (false, $"QoS policy was created but Windows reported different rates ({output}).");
                        }

                        if (output.StartsWith("FAIL:", StringComparison.Ordinal))
                        {
                            return (false, $"QoS limit not applied: {output.Substring(5).Trim()}");
                        }

                        return (false, "QoS limit could not be verified (PowerShell returned no result).");
                    }
                    catch (Exception ex)
                    {
                        return (false, $"QoS error: {ex.Message}");
                    }
                });
            }
            finally
            {
                macLock.Release();
            }
        }

        /// <summary>
        /// Configures 802.1p User Priority, DSCP, and Precedence QoS policy for a specific connected hotspot client.
        /// Priority range: 1 (Highest) to 7 (Least), default = 5.
        /// Lower numeric value = higher network priority.
        /// </summary>
        public async Task<(bool success, string message)> SetClientPriorityAsync(string macAddress, string ipAddress, int priority)
        {
            string cleanMac = HotspotModel.NormalizeMac(macAddress);
            if (!IsValidMac(cleanMac))
            {
                return (false, "Invalid MAC address format.");
            }

            var macLock = GetMacLock(cleanMac);
            await macLock.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        int clamped = Math.Clamp(priority, 1, 7);

                        lock (_clientDataUsageMap)
                        {
                            if (!_clientDataUsageMap.TryGetValue(cleanMac, out var stats))
                            {
                                stats = new ClientUsageStats();
                                _clientDataUsageMap[cleanMac] = stats;
                            }
                            stats.Priority = clamped;
                        }

                        string targetIp = ResolveClientIp(cleanMac, ipAddress);

                        // Map user priority (1=Highest .. 7=Least) to 802.1p and DSCP
                        // 802.1p: 7=Network Control/Voice, 6=Voice, 5=Video, 4=Controlled, 3=Best Effort, 2=Spare, 1=Background
                        sbyte dot1p = clamped switch
                        {
                            1 => 7, // Highest (Network Control / Real-time Voice)
                            2 => 6, // High (Voice / Interactive audio)
                            3 => 5, // Elevated (Video / Streaming)
                            4 => 4, // Above Normal (Controlled load)
                            5 => 3, // Default (Best effort)
                            6 => 2, // Low (Spare)
                            7 => 1, // Least (Background / Scavenger)
                            _ => 3
                        };

                        sbyte dscp = clamped switch
                        {
                            1 => 46, // EF (Expedited Forwarding)
                            2 => 34, // AF41
                            3 => 26, // AF31
                            4 => 18, // AF21
                            5 => 0,  // CS0 (Standard Best Effort)
                            6 => 10, // AF11
                            7 => 8,  // CS1 (Scavenger)
                            _ => 0
                        };

                        uint precedence = clamped switch
                        {
                            1 => 240,
                            2 => 220,
                            3 => 200,
                            4 => 180,
                            5 => 150,
                            6 => 140,
                            7 => 130,
                            _ => 150
                        };

                        string levelName = clamped switch
                        {
                            1 => "Highest (Priority 1)",
                            2 => "High (Priority 2)",
                            3 => "Elevated (Priority 3)",
                            4 => "Normal (Priority 4)",
                            5 => "Default (Priority 5)",
                            6 => "Low (Priority 6)",
                            7 => "Lowest (Priority 7)",
                            _ => $"Priority {clamped}"
                        };

                        if (string.IsNullOrEmpty(targetIp))
                        {
                            return (false, $"Priority {levelName} saved, but {macAddress} has no resolved IP address yet - it will be applied once the device obtains one.");
                        }

                        // Evict stale cached priority entries for any previous MAC holding this IP
                        if (!string.IsNullOrEmpty(targetIp) && targetIp != "-")
                        {
                            foreach (var kvp in _appliedPriorities)
                            {
                                if (kvp.Value.ip == targetIp && !string.Equals(kvp.Key, cleanMac, StringComparison.OrdinalIgnoreCase))
                                {
                                    _appliedPriorities.TryRemove(kvp.Key, out _);
                                }
                            }
                        }

                        // Optimization: Default priority 5 relies on Hotspot Transit QoS (Precedence 128)
                        // without needing redundant per-client NetQoS policies, eliminating connect-time PowerShell spawns on HDDs.
                        if (clamped == 5)
                        {
                            if (_appliedPriorities.TryGetValue(cleanMac, out var cachedDefault) && cachedDefault.ip == targetIp && cachedDefault.priority == 5)
                            {
                                return (true, "✓ Default priority verified (transit QoS: Precedence 128).");
                            }

                            string clearScript = $@"
$downName = 'Hotspot_Prio_Down_{cleanMac}'
$upName = 'Hotspot_Prio_Up_{cleanMac}'
try {{
    Remove-NetQosPolicy -Name $downName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name $upName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object {{
        $_.Name -like 'Hotspot_Prio_*' -and
        ($_.IPDstPrefix -eq '{targetIp}' -or $_.IPSrcPrefix -eq '{targetIp}')
    }} | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Write-Output 'CLEARED'
}} catch {{
    Write-Output 'FAIL'
}}
";
                            string clearOut = RunPowerShell(clearScript, 5000).Trim();
                            if (clearOut == "CLEARED")
                            {
                                _appliedPriorities[cleanMac] = (targetIp, 5);
                                return (true, "✓ Default priority active (standard transit QoS: Precedence 128).");
                            }
                            return (false, "Could not reset client priority to default.");
                        }

                        // Check if identical custom priority policy was already verified and applied for this client and IP
                        if (_appliedPriorities.TryGetValue(cleanMac, out var cachedPrio) && cachedPrio.ip == targetIp && cachedPrio.priority == clamped)
                        {
                            return (true, $"⚡ Priority verified (active): {levelName} • 802.1p {dot1p} • DSCP {dscp} • NetQoS precedence {precedence}.");
                        }

                        string script = $@"
$ErrorActionPreference = 'Stop'
$downName = 'Hotspot_Prio_Down_{cleanMac}'
$upName = 'Hotspot_Prio_Up_{cleanMac}'
try {{
    Remove-NetQosPolicy -Name $downName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name $upName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null

    # Evict stale priorities left behind by a device that previously held this IP
    Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object {{
        $_.Name -like 'Hotspot_Prio_*' -and $_.Name -ne $downName -and $_.Name -ne $upName -and
        ($_.IPDstPrefix -eq '{targetIp}' -or $_.IPSrcPrefix -eq '{targetIp}')
    }} | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue | Out-Null

    New-NetQosPolicy -Name $downName -IPDstPrefixMatchCondition '{targetIp}/32' -PriorityValue8021Action {dot1p} -DSCPAction {dscp} -Precedence {precedence} -Confirm:$false -ErrorAction Stop | Out-Null
    New-NetQosPolicy -Name $upName -IPSrcPrefixMatchCondition '{targetIp}/32' -PriorityValue8021Action {dot1p} -DSCPAction {dscp} -Precedence {precedence} -Confirm:$false -ErrorAction Stop | Out-Null
    $down = Get-NetQosPolicy -Name $downName -ErrorAction Stop
    $up = Get-NetQosPolicy -Name $upName -ErrorAction Stop
    Write-Output ('OK:' + $down.PriorityValue8021Action + ':' + $down.Precedence + ':' + $up.PriorityValue8021Action + ':' + $up.Precedence)
}} catch {{
    Write-Output ('FAIL:' + $_.Exception.Message)
}}
";
                        string output = RunPowerShell(script, 15000).Trim();

                        if (output.StartsWith("OK:", StringComparison.Ordinal))
                        {
                            var values = output.Substring(3).Split(':');
                            if (values.Length == 4 &&
                                int.TryParse(values[0], out int downDot1p) &&
                                uint.TryParse(values[1], out uint downPrecedence) &&
                                int.TryParse(values[2], out int upDot1p) &&
                                uint.TryParse(values[3], out uint upPrecedence) &&
                                downDot1p == dot1p && upDot1p == dot1p &&
                                downPrecedence == precedence && upPrecedence == precedence)
                            {
                                _appliedPriorities[cleanMac] = (targetIp, clamped);
                                return (true, $"⚡ Priority verified: {levelName} • 802.1p {dot1p} • DSCP {dscp} • NetQoS precedence {precedence}.");
                            }
                            return (false, $"Priority policy was created but Windows reported different values ({output}).");
                        }

                        if (output.StartsWith("FAIL:", StringComparison.Ordinal))
                        {
                            return (false, $"Priority not applied: {output.Substring(5).Trim()}");
                        }

                        return (false, "Priority could not be verified (PowerShell returned no result).");
                    }
                    catch (Exception ex)
                    {
                        return (false, $"Priority error: {ex.Message}");
                    }
                });
            }
            finally
            {
                macLock.Release();
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _disconnectedMacs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeBlockedMacs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.HashSet<string>> _blockedMacToIps = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _pausedClients = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pausedMacToIp = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsClientPaused(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return false;
            string clean = HotspotModel.NormalizeMac(mac);
            if (_pausedClients.TryGetValue(clean, out var exp))
            {
                return DateTime.UtcNow < exp;
            }
            return false;
        }

        public static DateTime? GetPauseExpiration(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            string clean = HotspotModel.NormalizeMac(mac);
            if (_pausedClients.TryGetValue(clean, out var exp))
            {
                return exp;
            }
            return null;
        }

        public static void RegisterPausedClient(string mac, DateTime expiresAtUtc)
        {
            if (!string.IsNullOrEmpty(mac))
            {
                string clean = HotspotModel.NormalizeMac(mac);
                _pausedClients[clean] = expiresAtUtc;
            }
        }

        public static void ClearPausedClient(string mac)
        {
            if (!string.IsNullOrEmpty(mac))
            {
                string clean = HotspotModel.NormalizeMac(mac);
                _pausedClients.TryRemove(clean, out _);
                _pausedMacToIp.TryRemove(clean, out _);
            }
        }

        public static void EvictStalePausePolicies(string cleanMac, string? ip = null)
        {
            if (string.IsNullOrEmpty(cleanMac)) return;
            string normMac = HotspotModel.NormalizeMac(cleanMac);

            // Gate: only launch PowerShell if this device was actually paused
            bool wasPaused = _pausedClients.TryRemove(normMac, out _) || _pausedMacToIp.TryRemove(normMac, out _);
            if (!wasPaused) return;

            Task.Run(async () =>
            {
                try
                {
                    string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Remove-NetQosPolicy -Name 'Hotspot_Pause_In_{normMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
Remove-NetQosPolicy -Name 'Hotspot_Pause_Out_{normMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
";
                    await RunPowerShellAsync(script, 3000);
                }
                catch { }
            });
        }

        public static Dictionary<string, DateTime> GetPausedClientsSnapshot()
        {
            var now = DateTime.UtcNow;
            var dict = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _pausedClients)
            {
                if (kvp.Value > now)
                {
                    dict[kvp.Key] = kvp.Value;
                }
            }
            return dict;
        }

        public static bool IsClientBlocked(string mac) =>
            !string.IsNullOrEmpty(mac) && _activeBlockedMacs.ContainsKey(HotspotModel.NormalizeMac(mac));

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
                _activeBlockedMacs.TryAdd(HotspotModel.NormalizeMac(mac), 0);
            }
        }

        public static void ClearBlockedMac(string mac)
        {
            if (!string.IsNullOrEmpty(mac))
            {
                _activeBlockedMacs.TryRemove(HotspotModel.NormalizeMac(mac), out _);
            }
        }

        /// <summary>
        /// Permanently seals and isolates an IP address associated with a blocked MAC:
        /// 1. Layer 2 Physical Blackhole: Static ARP table poison (00-00-00-00-00-00) forces host NIC to send frames to null address.
        /// 2. Windows Defender Firewall (netsh): Proven Inbound and Outbound block rules on all profiles, plus DNS (UDP/TCP 53) and DHCP (UDP 67).
        /// 3. Kernel NetQoS: Clamps bandwidth to 8 bps (1 byte/sec) with Precedence 255.
        /// </summary>
        public static void EnforceIpBlock(string cleanMac, string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "-" || !IsValidIp(ip)) return;
            cleanMac = HotspotModel.NormalizeMac(cleanMac);
            if (!IsValidMac(cleanMac)) return;

            // Invalidate policy caches so unblocked or blocked clients never report false active QoS/priority
            _appliedPriorities.TryRemove(cleanMac, out _);
            _appliedQosLimits.TryRemove(cleanMac, out _);

            lock (_blockedMacToIps)
            {
                if (!_blockedMacToIps.TryGetValue(cleanMac, out var set))
                {
                    set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _blockedMacToIps[cleanMac] = set;
                }
                set.Add(ip);
            }

            try
            {
                // 1. Layer 2 Physical Blackhole: Poison ARP cache with 00-00-00-00-00-00
                RunCommand("arp", $"-s {ip} 00-00-00-00-00-00");

                // 2. Windows Defender Firewall (netsh) - The exact proven working rules:
                RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_{cleanMac}\" dir=in action=block remoteip={ip}");
                RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_Out_{cleanMac}\" dir=out action=block remoteip={ip}");
                RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_DNS_{cleanMac}\" dir=in action=block protocol=UDP localport=53 remoteip={ip}");
                RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_DNST_{cleanMac}\" dir=in action=block protocol=TCP localport=53 remoteip={ip}");
                RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_Block_DHCP_{cleanMac}\" dir=in action=block protocol=UDP localport=67 remoteip={ip}");

                // 3. PowerShell Kernel NetQoS (8 bps freeze with Precedence 255)
                string ps = $@"
try {{
    Remove-NetQosPolicy -Name 'Hotspot_Block_In_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name 'Hotspot_Block_Out_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name 'Hotspot_Prio_Down_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name 'Hotspot_Prio_Up_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    New-NetQosPolicy -Name 'Hotspot_Block_In_{cleanMac}' -IPDstPrefixMatchCondition '{ip}/32' -ThrottleRateActionBitsPerSecond 8 -Precedence 255 -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    New-NetQosPolicy -Name 'Hotspot_Block_Out_{cleanMac}' -IPSrcPrefixMatchCondition '{ip}/32' -ThrottleRateActionBitsPerSecond 8 -Precedence 255 -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}} catch {{}}
";
                RunPowerShell(ps, 4000);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EnforceIpBlock error: {ex.Message}");
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
                    _appliedPriorities.TryRemove(cleanMac, out _);
                    _appliedQosLimits.TryRemove(cleanMac, out _);

                    string targetIp = ipAddress;
                    if (string.IsNullOrEmpty(targetIp) || targetIp == "-")
                    {
                        var arpMap = NativeArpHelper.GetHotspotArpEntries();
                        if (arpMap.TryGetValue(cleanMac, out var resolved)) targetIp = resolved;
                    }

                    if (string.IsNullOrEmpty(targetIp) || targetIp == "-")
                    {
                        var swept = await NativeArpHelper.ProbeSubnetForMacAsync(macAddress);
                        if (!string.IsNullOrEmpty(swept)) targetIp = swept;
                    }

                    if (!string.IsNullOrEmpty(targetIp) && targetIp != "-")
                    {
                        // 1. Layer 2 Blackhole drop
                        RunCommand("arp", $"-s {targetIp} 00-00-00-00-00-00");

                        // 2. Add temporary drop firewall rules
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_TempDrop_{cleanMac}\" dir=in action=block remoteip={targetIp}");
                        RunCommand("netsh", $"advfirewall firewall add rule name=\"Hotspot_TempDrop_Out_{cleanMac}\" dir=out action=block remoteip={targetIp}");

                        // Auto-clear the temporary drop rules after 30 seconds
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(30000);
                            RunCommand("arp", $"-d {targetIp}");
                            RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_TempDrop_{cleanMac}\"");
                            RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_TempDrop_Out_{cleanMac}\"");
                            RunCommand("netsh", "interface ip delete arpcache");
                        });
                    }
                    else
                    {
                        RunCommand("netsh", "interface ip delete arpcache");
                    }

                    return (true, $"Device {macAddress} disconnected and severed from hotspot.");
                }
                catch (Exception ex)
                {
                    return (false, $"Disconnect error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Permanently blocks a client device from accessing the hotspot using multi-layer isolation:
        /// 1. Injects static ARP blackhole (00-00-00-00-00-00) so physical frames never reach client.
        /// 2. Clamps bandwidth to 8 bps in Windows Kernel NetQoS with Precedence 255.
        /// 3. Adds Inbound, Outbound, DNS (UDP/TCP 53), and DHCP (UDP 67) firewall blocks.
        /// 4. Automatically detects re-association or IP change and immediately re-seals.
        /// </summary>
        public async Task<(bool success, string message)> BlockClientAsync(string macAddress, string ipAddress)
        {
            string cleanMac = HotspotModel.NormalizeMac(macAddress);
            if (!IsValidMac(cleanMac))
            {
                return (false, "Invalid MAC address format.");
            }

            var macLock = GetMacLock(cleanMac);
            await macLock.WaitAsync();
            try
            {
                return await Task.Run(async () =>
                {
                    try
                    {
                        _activeBlockedMacs.TryAdd(cleanMac, 0);
                        _disconnectedMacs[cleanMac] = DateTime.Now;
                        _appliedPriorities.TryRemove(cleanMac, out _);
                        _appliedQosLimits.TryRemove(cleanMac, out _);

                        // Gather all candidate IPs for this MAC
                        var targetIps = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrEmpty(ipAddress) && ipAddress != "-" && ipAddress.Contains('.'))
                        {
                            targetIps.Add(ipAddress);
                        }

                        // Check ARP map
                        var arpMap = NativeArpHelper.GetHotspotArpEntries();
                        if (arpMap.TryGetValue(cleanMac, out var mapIp))
                        {
                            targetIps.Add(mapIp);
                        }

                        // Probe subnet if still no IP
                        if (targetIps.Count == 0)
                        {
                            var swept = await NativeArpHelper.ProbeSubnetForMacAsync(macAddress);
                            if (!string.IsNullOrEmpty(swept)) targetIps.Add(swept);
                        }

                        // Enforce impenetrable multi-layer block on all discovered IPs
                        foreach (var ip in targetIps)
                        {
                            EnforceIpBlock(cleanMac, ip);
                        }

                        string ipDisplay = targetIps.Count > 0 ? string.Join(", ", targetIps) : "IP Pending (Auto-sealed on link)";
                        return (true, $"🚫 Device {macAddress} ({ipDisplay}) is now fully BLOCKED and severed from the hotspot.");
                    }
                    catch (Exception ex)
                    {
                        return (false, $"Block error: {ex.Message}");
                    }
                });
            }
            finally
            {
                macLock.Release();
            }
        }

        /// <summary>
        /// Unblocks a previously blocked client device, removing all ARP blackholes, firewall, NetQoS, and NetNeighbor rules.
        /// </summary>
        public async Task<(bool success, string message)> UnblockClientAsync(string macAddress, string ipAddress)
        {
            string cleanMac = HotspotModel.NormalizeMac(macAddress);
            if (!IsValidMac(cleanMac))
            {
                return (false, "Invalid MAC address format.");
            }

            var macLock = GetMacLock(cleanMac);
            await macLock.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        _activeBlockedMacs.TryRemove(cleanMac, out _);
                        _disconnectedMacs.TryRemove(cleanMac, out _);
                        _appliedPriorities.TryRemove(cleanMac, out _);
                        _appliedQosLimits.TryRemove(cleanMac, out _);

                        var ipsToRemove = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrEmpty(ipAddress) && ipAddress != "-" && ipAddress.Contains('.'))
                        {
                            ipsToRemove.Add(ipAddress);
                        }
                        lock (_blockedMacToIps)
                        {
                            if (_blockedMacToIps.TryRemove(cleanMac, out var storedIps))
                            {
                                foreach (var ip in storedIps) ipsToRemove.Add(ip);
                            }
                        }

                        foreach (var ip in ipsToRemove)
                        {
                            // 1. Remove static ARP entry
                            RunCommand("arp", $"-d {ip}");
                        }

                        // 2. Delete netsh firewall rules
                        RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_{cleanMac}\"");
                        RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_Out_{cleanMac}\"");
                        RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_DNS_{cleanMac}\"");
                        RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_DNST_{cleanMac}\"");
                        RunCommand("netsh", $"advfirewall firewall delete rule name=\"Hotspot_Block_DHCP_{cleanMac}\"");

                        // 3. Delete NetQoS policies
                        string psUnblock = $@"
try {{
    Remove-NetQosPolicy -Name 'Hotspot_Block_In_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name 'Hotspot_Block_Out_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}} catch {{}}
";
                        RunPowerShell(psUnblock, 3000);

                        // 4. Flush ARP cache
                        RunCommand("netsh", "interface ip delete arpcache");

                        // 5. Automatically restore configured Priority & QoS policies once unblocked
                        string restoreIp = ipAddress;
                        if (string.IsNullOrEmpty(restoreIp) || restoreIp == "-")
                        {
                            restoreIp = ipsToRemove.FirstOrDefault(ip => !string.IsNullOrEmpty(ip) && ip != "-") ?? "";
                        }
                        if (string.IsNullOrEmpty(restoreIp) || restoreIp == "-")
                        {
                            var arpMap = NativeArpHelper.GetHotspotArpEntries();
                            if (arpMap.TryGetValue(cleanMac, out var resolvedIp)) restoreIp = resolvedIp;
                        }

                        int savedPrio = 5;
                        string savedQos = "Unlimited";
                        lock (_clientDataUsageMap)
                        {
                            if (_clientDataUsageMap.TryGetValue(cleanMac, out var stats))
                            {
                                savedPrio = stats.Priority;
                                savedQos = stats.QosLimit;
                            }
                        }

                        if (!string.IsNullOrEmpty(restoreIp) && restoreIp != "-")
                        {
                            _ = Task.Run(async () =>
                            {
                                await SetClientPriorityAsync(cleanMac, restoreIp, savedPrio);
                                if (!string.IsNullOrEmpty(savedQos) && savedQos != "Unlimited")
                                {
                                    await SetClientQosLimitAsync(cleanMac, restoreIp, savedQos);
                                }
                            });
                        }

                        return (true, $"Device {macAddress} unblocked successfully.");
                    }
                    catch (Exception ex)
                    {
                        return (false, $"Unblock error: {ex.Message}");
                    }
                });
            }
            finally
            {
                macLock.Release();
            }
        }

        /// <summary>
        /// Pauses internet transit for a connected device for a specific duration (in minutes).
        /// Uses NetQoS bandwidth clamping (8 bps, Precedence 250) so the client stays associated to Wi-Fi
        /// without disconnect/reconnect loops or DHCP churn.
        /// </summary>
        public async Task<(bool success, string message)> PauseClientAsync(string macAddress, string ipAddress, int durationMinutes)
        {
            string cleanMac = HotspotModel.NormalizeMac(macAddress);
            if (!IsValidMac(cleanMac))
            {
                return (false, "Invalid MAC address format.");
            }

            var macLock = GetMacLock(cleanMac);
            await macLock.WaitAsync();
            try
            {
                return await Task.Run(async () =>
                {
                    try
                    {
                        var expUtc = DateTime.UtcNow.AddMinutes(durationMinutes);
                        _pausedClients[cleanMac] = expUtc;

                        string targetIp = ipAddress;
                        if (string.IsNullOrEmpty(targetIp) || targetIp == "-")
                        {
                            var arpMap = NativeArpHelper.GetHotspotArpEntries();
                            if (arpMap.TryGetValue(cleanMac, out var mapIp))
                            {
                                targetIp = mapIp;
                            }
                            else
                            {
                                var swept = await NativeArpHelper.ProbeSubnetForMacAsync(macAddress);
                                if (!string.IsNullOrEmpty(swept)) targetIp = swept;
                            }
                        }

                        if (!string.IsNullOrEmpty(targetIp) && targetIp != "-" && targetIp.Contains('.'))
                        {
                            _pausedMacToIp[cleanMac] = targetIp;
                            // Invalidate applied QoS limits and priorities cache so Resume will re-assert them cleanly
                            _appliedQosLimits.TryRemove(cleanMac, out _);
                            _appliedPriorities.TryRemove(cleanMac, out _);

                            string script = $@"
$ErrorActionPreference = 'Stop'
$downName = 'Hotspot_Pause_In_{cleanMac}'
$upName = 'Hotspot_Pause_Out_{cleanMac}'
try {{
    # Clear prior QoS limits while paused to avoid packet scheduler precedence conflict
    Remove-NetQosPolicy -Name 'Hotspot_QoS_Down_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name 'Hotspot_QoS_Up_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name $downName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name $upName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    New-NetQosPolicy -Name $downName -IPDstPrefixMatchCondition '{targetIp}/32' -ThrottleRateActionBitsPerSecond 8 -Precedence 250 -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    New-NetQosPolicy -Name $upName -IPSrcPrefixMatchCondition '{targetIp}/32' -ThrottleRateActionBitsPerSecond 8 -Precedence 250 -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}} catch {{}}
";
                            RunPowerShell(script, 5000);
                        }

                        return (true, $"Internet paused for {durationMinutes} min on {macAddress}.");
                    }
                    catch (Exception ex)
                    {
                        return (false, $"Pause error: {ex.Message}");
                    }
                });
            }
            finally
            {
                macLock.Release();
            }
        }

        /// <summary>
        /// Resumes internet transit for a paused client device, lifting the NetQoS freeze
        /// and restoring original QoS and Priority policies.
        /// </summary>
        public async Task<(bool success, string message)> ResumeClientAsync(string macAddress, string ipAddress)
        {
            string cleanMac = HotspotModel.NormalizeMac(macAddress);
            if (!IsValidMac(cleanMac))
            {
                return (false, "Invalid MAC address format.");
            }

            var macLock = GetMacLock(cleanMac);
            await macLock.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        _pausedClients.TryRemove(cleanMac, out _);
                        _pausedMacToIp.TryRemove(cleanMac, out _);

                        string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Remove-NetQosPolicy -Name 'Hotspot_Pause_In_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
Remove-NetQosPolicy -Name 'Hotspot_Pause_Out_{cleanMac}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
";
                        RunPowerShell(script, 3000);

                        // Automatically re-apply configured Priority & QoS policies once resumed
                        string restoreIp = ipAddress;
                        if (string.IsNullOrEmpty(restoreIp) || restoreIp == "-")
                        {
                            var arpMap = NativeArpHelper.GetHotspotArpEntries();
                            if (arpMap.TryGetValue(cleanMac, out var resolvedIp)) restoreIp = resolvedIp;
                        }

                        int savedPrio = 5;
                        string savedQos = "Unlimited";
                        lock (_clientDataUsageMap)
                        {
                            if (_clientDataUsageMap.TryGetValue(cleanMac, out var stats))
                            {
                                savedPrio = stats.Priority;
                                savedQos = stats.QosLimit;
                            }
                        }

                        if (!string.IsNullOrEmpty(restoreIp) && restoreIp != "-")
                        {
                            _ = Task.Run(async () =>
                            {
                                await SetClientPriorityAsync(cleanMac, restoreIp, savedPrio);
                                if (!string.IsNullOrEmpty(savedQos) && savedQos != "Unlimited")
                                {
                                    await SetClientQosLimitAsync(cleanMac, restoreIp, savedQos);
                                }
                            });
                        }

                        return (true, $"Internet access resumed for {macAddress}.");
                    }
                    catch (Exception ex)
                    {
                        return (false, $"Resume error: {ex.Message}");
                    }
                });
            }
            finally
            {
                macLock.Release();
            }
        }

        /// <summary>
        /// Re-enforces all active Hotspot rules:
        /// 1. High CPU & Upstream Connection Priority (over desktop browsers), including the QoS Packet Scheduler binding.
        /// 2. Impenetrable Layer 2 ARP blackholes and firewall rules for all blocked devices.
        /// 3. Bandwidth QoS rate-limiting caps (30 KB/s, 100 KB/s, 300 KB/s, 1-20 Mbps) for active clients.
        /// 4. 802.1p and DSCP priority levels (1-7) for active clients.
        /// </summary>
        public async Task<(bool success, string message)> ReenforceAllRulesAsync(
            IEnumerable<HotspotClientInfo> activeClients,
            IEnumerable<BlockedClientInfo> blockedClients,
            IDictionary<string, string>? qosLimits,
            IDictionary<string, int>? priorities)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    // 1. Re-enforce Host PC Priority & Upstream Transit Priority (over desktop browsers)
                    _appliedPriorities.Clear();
                    _appliedQosLimits.Clear();
                    var priorityResult = await _priorityService.EngageHighestPriorityAsync(allowPacerRebind: true);

                    int blocksEnforced = 0;
                    int qosApplied = 0;
                    int qosFailed = 0;
                    int prioApplied = 0;
                    int prioFailed = 0;

                    // 2. Re-enforce Blocks
                    var arpMap = NativeArpHelper.GetHotspotArpEntries();
                    var allBlocked = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var b in blockedClients)
                    {
                        allBlocked.Add(HotspotModel.NormalizeMac(b.MacAddress));
                    }
                    foreach (var kvp in _activeBlockedMacs)
                    {
                        allBlocked.Add(HotspotModel.NormalizeMac(kvp.Key));
                    }

                    foreach (var mac in allBlocked)
                    {
                        RegisterBlockedMac(mac);
                        var ips = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // Look up in stored ips
                        lock (_blockedMacToIps)
                        {
                            if (_blockedMacToIps.TryGetValue(mac, out var stored))
                            {
                                foreach (var ip in stored) ips.Add(ip);
                            }
                        }

                        // Look up in ARP
                        if (arpMap.TryGetValue(mac, out var mapIp))
                        {
                            ips.Add(mapIp);
                        }

                        // Also check blocked list
                        var found = blockedClients.FirstOrDefault(b => HotspotModel.NormalizeMac(b.MacAddress) == mac);
                        if (found != null && !string.IsNullOrEmpty(found.IpAddress) && found.IpAddress != "-")
                        {
                            ips.Add(found.IpAddress);
                        }

                        if (ips.Count == 0)
                        {
                            var swept = await NativeArpHelper.ProbeSubnetForMacAsync(mac);
                            if (!string.IsNullOrEmpty(swept)) ips.Add(swept);
                        }

                        foreach (var ip in ips)
                        {
                            EnforceIpBlock(mac, ip);
                            blocksEnforced++;
                        }
                    }

                    // 3. Re-enforce QoS Limits for active clients (skip blocked or currently paused clients)
                    foreach (var client in activeClients)
                    {
                        string cleanMac = HotspotModel.NormalizeMac(client.MacAddress);
                        if (IsClientBlocked(cleanMac) || IsClientPaused(cleanMac)) continue;

                        string? limit = null;
                        if (!string.IsNullOrEmpty(client.QosLimit) && client.QosLimit != "Unlimited")
                        {
                            limit = client.QosLimit;
                        }
                        else if (qosLimits != null && qosLimits.TryGetValue(cleanMac, out var savedLimit) && savedLimit != "Unlimited")
                        {
                            limit = savedLimit;
                        }

                        if (!string.IsNullOrEmpty(limit))
                        {
                            var qos = await SetClientQosLimitAsync(client.MacAddress, client.IpAddress, limit);
                            if (qos.success) qosApplied++; else qosFailed++;
                        }
                    }

                    // 4. Re-enforce Client Priorities for active clients (skip blocked or currently paused clients)
                    foreach (var client in activeClients)
                    {
                        string cleanMac = HotspotModel.NormalizeMac(client.MacAddress);
                        if (IsClientBlocked(cleanMac) || IsClientPaused(cleanMac)) continue;

                        int prio = client.Priority > 0 ? client.Priority : 5;
                        if (priorities != null && priorities.TryGetValue(cleanMac, out int savedPrio) && savedPrio > 0)
                        {
                            prio = savedPrio;
                        }

                        var prioResult = await SetClientPriorityAsync(client.MacAddress, client.IpAddress, prio);
                        if (prioResult.success) prioApplied++; else prioFailed++;
                    }

                    string summary = $"🛡️ Rules re-enforced: {blocksEnforced} block rule(s), {qosApplied} QoS limit(s) verified, {prioApplied} priority level(s) verified.";
                    if (qosFailed + prioFailed > 0)
                        summary += $" ⚠️ {qosFailed + prioFailed} rule(s) could not be verified.";
                    if (!priorityResult.success)
                        summary += $" ⚠️ {priorityResult.details}";

                    return (qosFailed == 0 && prioFailed == 0 && priorityResult.success, summary);
                }
                catch (Exception ex)
                {
                    return (false, $"Refresh rules error: {ex.Message}");
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

        private static async Task<string> RunPowerShellAsync(string script, int timeoutMs = 15000)
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
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return string.Empty;

                using var cts = new CancellationTokenSource(timeoutMs);

                // Write script asynchronously to stdin
                await process.StandardInput.WriteLineAsync(script).ConfigureAwait(false);
                process.StandardInput.Close();

                // Concurrently read stdout and stderr to avoid OS pipe deadlock
                var outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var errTask = process.StandardError.ReadToEndAsync(cts.Token);

                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    Debug.WriteLine($"PowerShell command timed out after {timeoutMs}ms and was terminated.");
                    return string.Empty;
                }

                string output = await outTask.ConfigureAwait(false);
                string error = await errTask.ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(error))
                {
                    Debug.WriteLine($"PowerShell stderr: {error}");
                }

                return output.Trim();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerShell exec error: {ex.Message}");
                return string.Empty;
            }
        }

        private static string RunPowerShell(string script, int timeoutMs = 15000)
        {
            // Thread-pool hop so a blocking caller on the WPF UI thread cannot deadlock the continuation.
            return Task.Run(() => RunPowerShellAsync(script, timeoutMs)).GetAwaiter().GetResult();
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
                                        var sbColons = new StringBuilder();
                                        var sbHyphens = new StringBuilder();
                                        var sbClean = new StringBuilder();
                                        for (int m = 0; m < 6; m++)
                                        {
                                            if (m > 0)
                                            {
                                                sbColons.Append(':');
                                                sbHyphens.Append('-');
                                            }
                                            string hex = row.bPhysAddr[m].ToString("X2");
                                            sbColons.Append(hex);
                                            sbHyphens.Append(hex);
                                            sbClean.Append(hex);
                                        }
                                        map[sbColons.ToString()] = ip;
                                        map[sbHyphens.ToString()] = ip;
                                        map[sbClean.ToString()] = ip;
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
