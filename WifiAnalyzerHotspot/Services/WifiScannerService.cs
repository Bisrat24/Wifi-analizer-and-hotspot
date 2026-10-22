using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class WifiScannerService
    {
        // Palette of vibrant colors for the spectrum analyzer curves
        private static readonly string[] NeonColors = new[]
        {
            "#00E5FF", // Cyan
            "#FF1744", // Neon Red
            "#00E676", // Neon Green
            "#FFEA00", // Bright Yellow
            "#D500F9", // Neon Purple
            "#FF9100", // Neon Orange
            "#00B0FF", // Light Blue
            "#76FF03", // Lime
            "#F50057", // Hot Pink
            "#1DE9B6", // Teal
            "#FF3D00", // Deep Orange
            "#651FFF"  // Electric Violet
        };

        #region Native WLAN API P/Invoke for triggering active scan
        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanOpenHandle(
            uint dwClientVersion,
            IntPtr pReserved,
            out uint pdwNegotiatedVersion,
            out IntPtr phClientHandle);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanCloseHandle(
            IntPtr hClientHandle,
            IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanEnumInterfaces(
            IntPtr hClientHandle,
            IntPtr pReserved,
            out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanScan(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            IntPtr pDot11Ssid,
            IntPtr pIeData,
            IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern void WlanFreeMemory(IntPtr pMemory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public int isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_INTERFACE_INFO_LIST
        {
            public uint dwNumberOfItems;
            public uint dwIndex;
            public WLAN_INTERFACE_INFO InterfaceInfo;
        }
        #endregion

        private static DateTime _lastHardwareScan = DateTime.MinValue;

        /// <summary>
        /// Triggers hardware Wi-Fi active scan safely (throttled to at most once per 20 seconds).
        /// </summary>
        public static void TriggerHardwareScan()
        {
            try
            {
                if ((DateTime.UtcNow - _lastHardwareScan).TotalSeconds < 20) return;
                _lastHardwareScan = DateTime.UtcNow;

                IntPtr hClient = IntPtr.Zero;
                uint negotiatedVersion;
                if (WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out hClient) == 0 && hClient != IntPtr.Zero)
                {
                    IntPtr pIfList = IntPtr.Zero;
                    if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pIfList) == 0 && pIfList != IntPtr.Zero)
                    {
                        uint numItems = (uint)Marshal.ReadInt32(pIfList);
                        IntPtr pInfo = new IntPtr(pIfList.ToInt64() + 8);
                        for (int i = 0; i < numItems; i++)
                        {
                            WLAN_INTERFACE_INFO info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(pInfo);
                            Guid g = info.InterfaceGuid;
                            WlanScan(hClient, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            pInfo = new IntPtr(pInfo.ToInt64() + Marshal.SizeOf<WLAN_INTERFACE_INFO>());
                        }
                        WlanFreeMemory(pIfList);
                    }
                    WlanCloseHandle(hClient, IntPtr.Zero);
                }
            }
            catch
            {
                // Fallback silently if wlanapi is unavailable
            }
        }

        /// <summary>
        /// Scans and returns all visible Wi-Fi networks with complete channel, band, bandwidth, and signal properties.
        /// </summary>
        public async Task<List<WifiNetwork>> ScanNetworksAsync()
        {
            return await Task.Run(() =>
            {
                TriggerHardwareScan();
                var connectedInfo = GetConnectedInterfaceInfo();
                string output = RunCommand("netsh", "wlan show networks mode=bssid");
                var networks = ParseNetworks(output, connectedInfo);
                return networks;
            });
        }

        /// <summary>
        /// Gets current connected Wi-Fi adapter and active connection info.
        /// </summary>
        public ConnectedInterfaceInfo GetConnectedInterfaceInfo()
        {
            var info = new ConnectedInterfaceInfo();
            try
            {
                string output = RunCommand("netsh", "wlan show interfaces");
                var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var parts = line.Split(new[] { ':' }, 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    switch (key)
                    {
                        case "Name":
                            info.InterfaceName = value;
                            break;
                        case "Description":
                            info.Description = value;
                            break;
                        case "State":
                            if (value.Equals("disconnected", StringComparison.OrdinalIgnoreCase))
                            {
                                info.Ssid = "Disconnected";
                            }
                            break;
                        case "SSID":
                            info.Ssid = value;
                            break;
                        case "AP BSSID":
                            info.Bssid = value.ToUpperInvariant();
                            break;
                        case "Band":
                            info.Band = value;
                            break;
                        case "Channel":
                            if (int.TryParse(value, out int ch)) info.Channel = ch;
                            break;
                        case "Radio type":
                            info.RadioType = FormatRadioType(value);
                            break;
                        case "Authentication":
                            info.Auth = value;
                            break;
                        case "Signal":
                            var sigMatch = Regex.Match(value, @"\d+");
                            if (sigMatch.Success && int.TryParse(sigMatch.Value, out int sig))
                                info.Signal = sig;
                            break;
                        case "Rssi":
                            if (int.TryParse(value, out int rssi))
                                info.Rssi = rssi;
                            break;
                        case "Receive rate (Mbps)":
                            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rx))
                                info.RxRateMbps = rx;
                            break;
                        case "Transmit rate (Mbps)":
                            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double tx))
                                info.TxRateMbps = tx;
                            break;
                        case "Physical address":
                            info.Mac = value.ToUpperInvariant();
                            break;
                    }
                }

                // If RSSI was not explicitly given, calculate approximate RSSI from Signal %
                if (info.Rssi == -100 && info.Signal > 0)
                {
                    info.Rssi = (info.Signal / 2) - 100;
                }

                if (string.IsNullOrEmpty(info.Band) && info.Channel > 0)
                {
                    info.Band = info.Channel > 14 ? "5 GHz" : "2.4 GHz";
                }

                // Query Local IP and Gateway
                foreach (var netIf in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (netIf.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                        netIf.OperationalStatus == OperationalStatus.Up)
                    {
                        var ipProps = netIf.GetIPProperties();
                        foreach (var addr in ipProps.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                info.Ipv4 = addr.Address.ToString();
                                break;
                            }
                        }
                        var gw = ipProps.GatewayAddresses.FirstOrDefault();
                        if (gw != null)
                        {
                            info.Gateway = gw.Address.ToString();
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching interface info: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(info.Ssid))
            {
                info.Ssid = "Disconnected / Wi-Fi Off";
            }

            return info;
        }

        private List<WifiNetwork> ParseNetworks(string output, ConnectedInterfaceInfo connectedInfo)
        {
            var list = new List<WifiNetwork>();
            var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            string currentSsid = string.Empty;
            string currentAuth = "WPA2-Personal";
            string currentEnc = "CCMP";
            WifiNetwork? currentNetwork = null;
            int colorIndex = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (trimmed.StartsWith("SSID ", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
                {
                    var parts = trimmed.Split(new[] { ':' }, 2);
                    currentSsid = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    currentNetwork = null;
                    continue;
                }

                if (trimmed.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
                {
                    currentAuth = trimmed.Split(new[] { ':' }, 2)[1].Trim();
                    continue;
                }

                if (trimmed.StartsWith("Encryption", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
                {
                    currentEnc = trimmed.Split(new[] { ':' }, 2)[1].Trim();
                    continue;
                }

                if (trimmed.StartsWith("BSSID ", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
                {
                    var bssidPart = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim().ToUpperInvariant();

                    currentNetwork = new WifiNetwork
                    {
                        Ssid = currentSsid,
                        Bssid = bssidPart,
                        Authentication = currentAuth,
                        Encryption = currentEnc,
                        ColorIndex = colorIndex++
                    };
                    list.Add(currentNetwork);
                    continue;
                }

                if (currentNetwork != null)
                {
                    if (trimmed.StartsWith("Signal", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
                    {
                        var match = Regex.Match(trimmed, @"\d+");
                        if (match.Success && int.TryParse(match.Value, out int sig))
                        {
                            currentNetwork.SignalPercent = sig;
                            currentNetwork.Rssi = (sig / 2) - 100; // Standard dBm approximation
                        }
                    }
                    else if (trimmed.StartsWith("Radio type", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
                    {
                        var radio = trimmed.Split(new[] { ':' }, 2)[1].Trim();
                        currentNetwork.RadioType = FormatRadioType(radio);
                    }
                    else if (trimmed.StartsWith("Band", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
                    {
                        currentNetwork.Band = trimmed.Split(new[] { ':' }, 2)[1].Trim();
                    }
                    else if (trimmed.StartsWith("Channel", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
                    {
                        var chVal = trimmed.Split(new[] { ':' }, 2)[1].Trim();
                        if (int.TryParse(chVal, out int ch))
                        {
                            currentNetwork.Channel = ch;
                            currentNetwork.FrequencyMHz = ChannelToFrequency(ch, currentNetwork.Band);
                            currentNetwork.ChannelWidthMHz = EstimateChannelWidth(currentNetwork.RadioType, currentNetwork.Band, ch);
                        }
                    }
                }
            }

            // Cross-reference with connected interface to mark IsConnected and exact RSSI
            foreach (var net in list)
            {
                if (!string.IsNullOrEmpty(connectedInfo.Bssid) &&
                    net.Bssid.Equals(connectedInfo.Bssid, StringComparison.OrdinalIgnoreCase))
                {
                    net.IsConnected = true;
                    if (connectedInfo.Rssi > -100)
                    {
                        net.Rssi = connectedInfo.Rssi;
                    }
                }

                // Ensure Band is ALWAYS populated for every network!
                if (string.IsNullOrEmpty(net.Band))
                {
                    net.Band = net.Channel > 14 ? "5 GHz" : "2.4 GHz";
                }
            }

            // Order by Signal Strength descending
            return list.OrderByDescending(n => n.SignalPercent).ToList();
        }

        private static string FormatRadioType(string rawRadio)
        {
            rawRadio = rawRadio.Trim();
            if (rawRadio.Contains("802.11ax", StringComparison.OrdinalIgnoreCase)) return "802.11ax (Wi-Fi 6)";
            if (rawRadio.Contains("802.11be", StringComparison.OrdinalIgnoreCase)) return "802.11be (Wi-Fi 7)";
            if (rawRadio.Contains("802.11ac", StringComparison.OrdinalIgnoreCase)) return "802.11ac (Wi-Fi 5)";
            if (rawRadio.Contains("802.11n", StringComparison.OrdinalIgnoreCase)) return "802.11n (Wi-Fi 4)";
            if (rawRadio.Contains("802.11g", StringComparison.OrdinalIgnoreCase)) return "802.11g";
            if (rawRadio.Contains("802.11a", StringComparison.OrdinalIgnoreCase)) return "802.11a";
            if (rawRadio.Contains("802.11b", StringComparison.OrdinalIgnoreCase)) return "802.11b";
            return rawRadio;
        }

        public static int ChannelToFrequency(int channel, string band)
        {
            if (channel >= 1 && channel <= 14)
            {
                return channel == 14 ? 2484 : 2407 + (channel * 5);
            }
            if (channel >= 32 && channel <= 177)
            {
                return 5000 + (channel * 5);
            }
            return 2412;
        }

        public static int EstimateChannelWidth(string radioType, string band, int channel)
        {
            if (radioType.Contains("Wi-Fi 6") || radioType.Contains("802.11ax"))
            {
                return band.Contains("5") ? 80 : 40;
            }
            if (radioType.Contains("Wi-Fi 5") || radioType.Contains("802.11ac"))
            {
                return 80;
            }
            if (radioType.Contains("802.11n"))
            {
                return 20; // 20 MHz or 40 MHz for 802.11n
            }
            return 20;
        }

        private SolidColorBrush GetColorBrush(int index)
        {
            string hex = NeonColors[index % NeonColors.Length];
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
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
                process.WaitForExit(4000);
                return output;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Command error: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
