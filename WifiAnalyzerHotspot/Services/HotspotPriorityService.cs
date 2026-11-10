using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WifiAnalyzerHotspot.Services
{
    public class HotspotPriorityService
    {
        private static bool _isPriorityEngaged = false;

        public static bool IsPriorityEngaged => _isPriorityEngaged;

        /// <summary>
        /// Elevates network priority for the Hotspot without destabilizing the Windows desktop:
        /// 1. Sets process priority to High (optimal responsiveness without starving OS input/audio/DWM).
        /// 2. Optimizes Hotspot Virtual Adapter route metric (Metric = 5) for highest routing priority.
        /// 3. Prioritizes hotspot transit traffic (192.168.137.0/24) via NetQoS (Precedence 128, 802.1p Tag 7, DSCP 46).
        /// 4. Prioritizes hotspot traffic ahead of desktop browsers (Precedence 64).
        /// </summary>
        public async Task<(bool success, string details)> EngageHighestPriorityAsync()
        {
            return await Task.Run(async () =>
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

                    // 2. System-level NetQoS & adapter routing via PowerShell script
                    string script = @"
$ErrorActionPreference = 'SilentlyContinue'

# A. Prioritize Hotspot Virtual Adapter Route Metric (Metric = 5)
try {
    Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { ($_.InterfaceAlias -like '*Wi-Fi Direct*' -or $_.InterfaceAlias -like '*Local Area Connection*') -and $_.ConnectionState -eq 'Connected' } |
        Set-NetIPInterface -InterfaceMetric 5 -ErrorAction SilentlyContinue
} catch {}

# B. Prioritize all Hotspot Transit Traffic across the Main Shared Connection (Precedence 128):
# Precedence 128 ensures hotspot transit is prioritized above desktop browsers (64),
# while allowing specific client priorities (130-240) and blocks (255) to take full precedence!
try {
    Remove-NetQosPolicy -Name 'Hotspot_Transit_Up' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Remove-NetQosPolicy -Name 'Hotspot_Transit_Down' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    New-NetQosPolicy -Name 'Hotspot_Transit_Up' -IPSrcPrefixMatchCondition '192.168.137.0/24' -PriorityValue8021Action 7 -DSCPAction 46 -Precedence 128 -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    New-NetQosPolicy -Name 'Hotspot_Transit_Down' -IPDstPrefixMatchCondition '192.168.137.0/24' -PriorityValue8021Action 7 -DSCPAction 46 -Precedence 128 -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
} catch {}

# C. Ensure local browsers (Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Tor) do not choke upstream shared connection:
# Set desktop browsers to lower Precedence (64), 802.1p 1 (Background), and DSCP 8 (CS1 scavenger)
try {
    $browsers = @('chrome.exe', 'msedge.exe', 'firefox.exe', 'brave.exe', 'opera.exe', 'vivaldi.exe', 'tor.exe')
    foreach ($b in $browsers) {
        Remove-NetQosPolicy -Name ""Hotspot_Browser_$b"" -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
        New-NetQosPolicy -Name ""Hotspot_Browser_$b"" -AppPathNameMatchCondition ""*$b"" -PriorityValue8021Action 1 -DSCPAction 8 -Precedence 64 -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    }
} catch {}

Write-Output 'OK'
";
                    await RunPowerShellAsync(script, 10000);
                    _isPriorityEngaged = true;

                    return (true, "⚡ High Priority Mode Active: High CPU Priority • Upstream Connection Top Priority (over Browsers) • Optimized Route Metric");
                }
                catch (Exception ex)
                {
                    return (false, $"Priority optimization warning: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Releases priority and removes custom NetQoS policies when Hotspot stops.
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

                // Clear transit and browser NetQoS policies
                try
                {
                    string cleanupScript = @"
Remove-NetQosPolicy -Name 'Hotspot_Transit_Up' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
Remove-NetQosPolicy -Name 'Hotspot_Transit_Down' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
$browsers = @('chrome.exe', 'msedge.exe', 'firefox.exe', 'brave.exe', 'opera.exe', 'vivaldi.exe', 'tor.exe')
foreach ($b in $browsers) {
    Remove-NetQosPolicy -Name ""Hotspot_Browser_$b"" -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}
";
                    _ = RunPowerShellAsync(cleanupScript, 6000);
                }
                catch { }

                _isPriorityEngaged = false;
            }
            catch { }
        }

        private static async Task<string> RunPowerShellAsync(string script, int timeoutMs = 10000)
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
                if (proc == null) return string.Empty;

                using var cts = new CancellationTokenSource(timeoutMs);

                // Write script to stdin
                await proc.StandardInput.WriteLineAsync(script);
                proc.StandardInput.Close();

                // Concurrently read stdout and stderr to prevent deadlocks
                var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var errTask = proc.StandardError.ReadToEndAsync(cts.Token);

                await proc.WaitForExitAsync(cts.Token);
                string output = await outTask;
                return output.Trim();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HotspotPriorityService RunPowerShell error: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
