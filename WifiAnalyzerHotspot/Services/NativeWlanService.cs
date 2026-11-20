using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Native WLAN API access for Secure Connect. Replaces localized netsh text parsing with
    /// structured data; all calls are best-effort and return empty on unsupported drivers.
    /// </summary>
    internal static class NativeWlanService
    {
        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanGetAvailableNetworkList(IntPtr handle, ref Guid guid, uint flags, IntPtr reserved, out IntPtr list);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanGetProfileList(IntPtr handle, ref Guid guid, IntPtr reserved, out IntPtr list);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanConnect(IntPtr handle, ref Guid guid, ref WLAN_CONNECTION_PARAMETERS parameters, IntPtr reserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        private static extern uint WlanQueryInterface(
            IntPtr handle, ref Guid guid, int opcode, IntPtr reserved,
            out int dataSize, out IntPtr data, out IntPtr opcodeValueType);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_INTERFACE_INFO_LIST
        {
            public uint Count;
            public uint Index;
            public WLAN_INTERFACE_INFO InterfaceInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DOT11_SSID
        {
            public uint Length;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_AVAILABLE_NETWORK
        {
            public DOT11_SSID Ssid;
            public uint BssType;
            [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
            public uint SignalQuality;
            [MarshalAs(UnmanagedType.Bool)] public bool DefaultProfile;
            public uint AuthAlgorithm;
            public uint CipherAlgorithm;
            public uint Flags;
            public byte PreferredChannel;
            public uint RadioType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public uint[] Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_AVAILABLE_NETWORK_LIST
        {
            public uint Count;
            public uint Index;
            public WLAN_AVAILABLE_NETWORK Network;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_PROFILE_INFO
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_PROFILE_INFO_LIST
        {
            public uint Count;
            public uint Index;
            public WLAN_PROFILE_INFO Profile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_PARAMETERS
        {
            public uint Mode; // 0 = profile
            [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
            public IntPtr Ssid;
            public IntPtr BssidList;
            public uint BssType;
            public uint Flags;
        }

        // Only the leading fields are needed; reading a prefix of the native struct is safe.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_ATTRIBUTES_MIN
        {
            public int State; // 1 = connected
            public int ConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Profile;
            public DOT11_SSID Ssid;
        }

        private static bool TryOpen(out IntPtr handle, out List<Guid> interfaces)
        {
            handle = IntPtr.Zero;
            interfaces = new List<Guid>();
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0 || handle == IntPtr.Zero) return false;
                IntPtr listPtr = IntPtr.Zero;
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out listPtr) != 0 || listPtr == IntPtr.Zero) return false;
                var list = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(listPtr);
                IntPtr item = new IntPtr(listPtr.ToInt64() + 8);
                int size = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                for (int i = 0; i < list.Count; i++)
                {
                    interfaces.Add(Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(item).InterfaceGuid);
                    item = new IntPtr(item.ToInt64() + size);
                }
                WlanFreeMemory(listPtr);
                return interfaces.Count > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeWlan] open failed: {ex.Message}");
                return false;
            }
        }

        internal static List<WifiNetwork> GetAvailableNetworks()
        {
            var result = new List<WifiNetwork>();
            if (!TryOpen(out var handle, out var interfaces)) return result;
            try
            {
                int color = 0;
                foreach (var guid in interfaces)
                {
                    var ifaceGuid = guid;
                    IntPtr listPtr = IntPtr.Zero;
                    if (WlanGetAvailableNetworkList(handle, ref ifaceGuid, 0, IntPtr.Zero, out listPtr) != 0 || listPtr == IntPtr.Zero) continue;
                    try
                    {
                        var list = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK_LIST>(listPtr);
                        IntPtr item = new IntPtr(listPtr.ToInt64() + 8);
                        int size = Marshal.SizeOf<WLAN_AVAILABLE_NETWORK>();
                        for (int i = 0; i < list.Count; i++)
                        {
                            var net = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK>(item);
                            item = new IntPtr(item.ToInt64() + size);

                            int length = (int)Math.Min(net.Ssid.Length, 32);
                            string ssid = length > 0 && net.Ssid.Bytes != null
                                ? Encoding.UTF8.GetString(net.Ssid.Bytes, 0, length)
                                : string.Empty;
                            if (string.IsNullOrWhiteSpace(ssid)) continue;

                            int signal = (int)Math.Clamp(net.SignalQuality, 0, 100);
                            int channel = net.PreferredChannel > 0 ? net.PreferredChannel : 1;
                            string band = channel <= 14 ? "2.4 GHz" : "5 GHz";
                            string auth = MapAuth(net.AuthAlgorithm, net.SecurityEnabled);

                            result.Add(new WifiNetwork
                            {
                                Ssid = ssid,
                                SignalPercent = signal,
                                Rssi = (signal / 2) - 100,
                                Band = band,
                                Channel = channel,
                                FrequencyMHz = band.StartsWith("2.4") ? 2407 + (channel * 5) : 5000 + (channel * 5),
                                ChannelWidthMHz = 20,
                                Authentication = auth,
                                Encryption = MapCipher(net.CipherAlgorithm),
                                RadioType = "802.11ax",
                                IsSaved = !string.IsNullOrWhiteSpace(net.ProfileName),
                                ColorIndex = color++
                            });
                        }
                    }
                    finally { WlanFreeMemory(listPtr); }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[NativeWlan] scan failed: {ex.Message}"); }
            finally
            {
                WlanCloseHandle(handle, IntPtr.Zero);
            }
            return result.DistinctBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(n => n.SignalPercent).ToList();
        }

        internal static List<string> GetProfileNames()
        {
            var names = new List<string>();
            if (!TryOpen(out var handle, out var interfaces)) return names;
            try
            {
                foreach (var guid in interfaces)
                {
                    var ifaceGuid = guid;
                    IntPtr listPtr = IntPtr.Zero;
                    if (WlanGetProfileList(handle, ref ifaceGuid, IntPtr.Zero, out listPtr) != 0 || listPtr == IntPtr.Zero) continue;
                    try
                    {
                        var list = Marshal.PtrToStructure<WLAN_PROFILE_INFO_LIST>(listPtr);
                        IntPtr item = new IntPtr(listPtr.ToInt64() + 8);
                        int size = Marshal.SizeOf<WLAN_PROFILE_INFO>();
                        for (int i = 0; i < list.Count; i++)
                        {
                            string name = Marshal.PtrToStructure<WLAN_PROFILE_INFO>(item).Name;
                            item = new IntPtr(item.ToInt64() + size);
                            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                        }
                    }
                    finally { WlanFreeMemory(listPtr); }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[NativeWlan] profile list failed: {ex.Message}"); }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
            return names;
        }

        private static Guid SelectInterface(List<Guid> interfaces, string? adapterName, IntPtr handle)
        {
            if (!string.IsNullOrWhiteSpace(adapterName))
            {
                foreach (var guid in interfaces)
                {
                    IntPtr listPtr = IntPtr.Zero;
                    if (WlanEnumInterfaces(handle, IntPtr.Zero, out listPtr) != 0 || listPtr == IntPtr.Zero) break;
                    try
                    {
                        var list = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(listPtr);
                        IntPtr item = new IntPtr(listPtr.ToInt64() + 8);
                        int size = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                        for (int i = 0; i < list.Count; i++)
                        {
                            var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(item);
                            item = new IntPtr(item.ToInt64() + size);
                            if (info.InterfaceGuid == guid &&
                                info.Description.IndexOf(adapterName, StringComparison.OrdinalIgnoreCase) >= 0)
                                return guid;
                        }
                    }
                    finally { WlanFreeMemory(listPtr); }
                }
            }
            return interfaces.Count > 0 ? interfaces[0] : Guid.Empty;
        }

        private static (bool connected, string ssid, string profile) QueryCurrentConnection(IntPtr handle, Guid guid)
        {
            try
            {
                IntPtr data = IntPtr.Zero;
                uint rc = WlanQueryInterface(handle, ref guid, 7 /* current_connection */, IntPtr.Zero,
                    out _, out data, out _);
                if (rc != 0 || data == IntPtr.Zero)
                    return (false, string.Empty, new Win32Exception((int)rc).Message);

                try
                {
                    var attr = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES_MIN>(data);
                    int length = (int)Math.Min(attr.Ssid.Length, 32);
                    string ssid = length > 0 && attr.Ssid.Bytes != null
                        ? Encoding.UTF8.GetString(attr.Ssid.Bytes, 0, length)
                        : string.Empty;
                    return (attr.State == 1, ssid, attr.Profile ?? string.Empty);
                }
                finally { WlanFreeMemory(data); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeWlan] query current connection failed: {ex.Message}");
                return (false, string.Empty, string.Empty);
            }
        }

        internal static (bool connected, string ssid) GetCurrentConnection(string? adapterName = null)
        {
            if (!TryOpen(out var handle, out var interfaces)) return (false, string.Empty);
            try
            {
                var guid = SelectInterface(interfaces, adapterName, handle);
                if (guid == Guid.Empty) return (false, string.Empty);
                var (connected, ssid, _) = QueryCurrentConnection(handle, guid);
                return (connected, ssid);
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }

        internal static async Task<(bool success, string message)> ConnectToProfileAsync(
            string profileName, string? adapterName = null, int timeoutMs = 6000)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return (false, "Invalid profile name.");
            if (!TryOpen(out var handle, out var interfaces))
                return (false, "Native WLAN API is unavailable; using netsh fallback.");

            try
            {
                var guid = SelectInterface(interfaces, adapterName, handle);
                if (guid == Guid.Empty) return (false, "No Wi-Fi adapter found.");

                var parameters = new WLAN_CONNECTION_PARAMETERS
                {
                    Mode = 0,
                    Profile = profileName,
                    BssType = 1, // infrastructure
                    Flags = 0
                };
                uint error = WlanConnect(handle, ref guid, ref parameters, IntPtr.Zero);
                if (error != 0)
                    return (false, new Win32Exception((int)error).Message);

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                string lastSsid = string.Empty;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(400).ConfigureAwait(false);
                    var (connected, ssid, profile) = QueryCurrentConnection(handle, guid);
                    lastSsid = ssid;
                    if (connected && (ssid.Equals(profileName, StringComparison.OrdinalIgnoreCase) ||
                                      profile.Equals(profileName, StringComparison.OrdinalIgnoreCase)))
                        return (true, $"Connected to '{profileName}'.");
                }
                return (false, string.IsNullOrWhiteSpace(lastSsid)
                    ? "Connection did not complete."
                    : $"Connection not confirmed (current network: {lastSsid}).");
            }
            catch (Exception ex)
            {
                return (false, $"Connection failed: {ex.Message}");
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }

        private static string MapAuth(uint algorithm, bool secured)
        {
            // DOT11_AUTH_ALGORITHM values used by the WLAN API.
            return algorithm switch
            {
                1 or 2 => "Open",
                3 or 4 => "WPA-Personal",
                5 => "WPA2-Enterprise",
                6 => "WPA2-Personal",
                7 => "WPA3-Enterprise",
                8 or 9 => "WPA3-Personal",
                _ => secured ? "WPA2-Personal" : "Open"
            };
        }

        private static string MapCipher(uint algorithm)
        {
            return algorithm switch
            {
                0x01 => "WEP",
                0x02 => "TKIP",
                0x04 => "AES (CCMP)",
                0x06 => "AES (CCMP-256)",
                _ => "AES"
            };
        }
    }
}
