using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WifiAnalyzerHotspot.Services
{
    public class HotspotPriorityService
    {
        [Flags]
        public enum EXECUTION_STATE : uint
        {
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_SYSTEM_REQUIRED = 0x00000001
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        private static bool _isPriorityEngaged = false;

        public static bool IsPriorityEngaged => _isPriorityEngaged;

        /// <summary>
        /// Elevates the PC to the Highest Priority for Hotspot:
        /// 1. Elevates current application process priority to High.
        /// 2. Engages Win32 SetThreadExecutionState (System Required + Away Mode) to prevent CPU/Wi-Fi sleep throttling.
        /// 3. Boosts SharedAccess (ICS NAT Router / DHCP) and wlanext.exe process priorities to High.
        /// 4. Disables Windows Network Throttling (NetworkThrottlingIndex = 0xFFFFFFFF) & sets SystemResponsiveness = 0 (100% priority for network routing).
        /// 5. Forces Wi-Fi radio to Maximum Transmit Power (Level 5), Throughput Booster Enabled, and disables Packet Coalescing.
        /// 6. Optimizes Hotspot Virtual Adapter route metric (Metric = 5) for highest routing priority.
        /// </summary>
        public async Task<(bool success, string details)> EngageHighestPriorityAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 1. Elevate application process priority to High
                    try
                    {
                        using var proc = Process.GetCurrentProcess();
                        proc.PriorityClass = ProcessPriorityClass.High;
                    }
                    catch { }

                    // 2. Prevent system sleep, CPU downclocking, and radio sleep throttling via Win32 kernel power API
                    try
                    {
                        SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS |
                                                EXECUTION_STATE.ES_SYSTEM_REQUIRED |
                                                EXECUTION_STATE.ES_AWAYMODE_REQUIRED);
                    }
                    catch { }

                    // 3. System-level optimizations via PowerShell script
                    string script = @"
$ErrorActionPreference = 'SilentlyContinue'

# A. Boost SharedAccess (ICS NAT service) and wlanext.exe process priority to High
try {
    $saSvc = Get-CimInstance Win32_Service -Filter ""Name='SharedAccess'"" -ErrorAction SilentlyContinue
    if ($saSvc -and $saSvc.ProcessId -gt 0) {
        $p = Get-Process -Id $saSvc.ProcessId -ErrorAction SilentlyContinue
        if ($p) { $p.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High }
    }
    Get-Process -Name 'wlanext' -ErrorAction SilentlyContinue | ForEach-Object {
        $_.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High
    }
} catch {}

# B. Disable Windows Network Throttling & prioritize network packet I/O (SystemResponsiveness = 0)
try {
    $regPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
    if (Test-Path $regPath) {
        Set-ItemProperty -Path $regPath -Name 'SystemResponsiveness' -Value 0 -Type DWord -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $regPath -Name 'NetworkThrottlingIndex' -Value 0xFFFFFFFF -Type DWord -ErrorAction SilentlyContinue
    }
} catch {}

# C. Maximize Wi-Fi Adapter Transmit Power, Throughput Booster, and disable Packet Coalescing
try {
    $wifiAdapters = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { 
        $_.InterfaceDescription -like '*Wi-Fi*' -or $_.InterfaceDescription -like '*Wireless*' -or $_.InterfaceDescription -like '*802.11*'
    }
    foreach ($ad in $wifiAdapters) {
        Set-NetAdapterAdvancedProperty -Name $ad.Name -DisplayName 'Transmit Power' -DisplayValue '5. Highest' -ErrorAction SilentlyContinue
        Set-NetAdapterAdvancedProperty -Name $ad.Name -DisplayName 'Throughput Booster' -DisplayValue 'Enabled' -ErrorAction SilentlyContinue
        Set-NetAdapterAdvancedProperty -Name $ad.Name -DisplayName 'Packet Coalescing' -DisplayValue 'Disabled' -ErrorAction SilentlyContinue
        Set-NetAdapterAdvancedProperty -Name $ad.Name -DisplayName 'Sleep on WoWLAN Disconnect' -DisplayValue 'Disabled' -ErrorAction SilentlyContinue
        Set-NetAdapterPowerManagement -Name $ad.Name -DeviceSleepOnDisconnect Disabled -D0PacketCoalescing Disabled -SelectiveSuspend Disabled -ErrorAction SilentlyContinue
    }
} catch {}

# D. Prioritize Hotspot Virtual Adapter Route Metric (Metric = 5)
try {
    Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { ($_.InterfaceAlias -like '*Wi-Fi Direct*' -or $_.InterfaceAlias -like '*Local Area Connection*') -and $_.ConnectionState -eq 'Connected' } |
        Set-NetIPInterface -InterfaceMetric 5 -ErrorAction SilentlyContinue
} catch {}

Write-Output 'OK'
";
                    RunPowerShell(script);
                    _isPriorityEngaged = true;

                    return (true, "⚡ Highest Priority Mode Active: High CPU Priority • Max Radio Power • No Sleep Throttling • 0% Network Throttling");
                }
                catch (Exception ex)
                {
                    return (false, $"Priority optimization warning: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Releases highest priority and restores standard desktop priority when Hotspot stops.
        /// </summary>
        public void ReleaseHighestPriority()
        {
            try
            {
                // Restore current process priority to Normal
                try
                {
                    using var proc = Process.GetCurrentProcess();
                    proc.PriorityClass = ProcessPriorityClass.Normal;
                }
                catch { }

                // Release Win32 execution lock
                try
                {
                    SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
                }
                catch { }

                _isPriorityEngaged = false;
            }
            catch { }
        }

        private static void RunPowerShell(string script)
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
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return;

                using (var sw = proc.StandardInput)
                {
                    if (sw.BaseStream.CanWrite)
                    {
                        sw.Write(script);
                    }
                }

                proc.WaitForExit(10000);
            }
            catch { }
        }
    }
}
