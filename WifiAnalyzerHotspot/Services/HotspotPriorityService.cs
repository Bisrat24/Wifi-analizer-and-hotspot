using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WifiAnalyzerHotspot.Services
{
    public class HotspotPriorityService
    {
        private static bool _isPriorityEngaged = false;
        private static bool _priorityVlanConfigured = false;

        public static bool IsPriorityEngaged => _isPriorityEngaged;
        public static bool PreserveGigabitLinkSpeed { get; set; } = false;
        public static bool EnableAntiBufferbloat { get; set; } = false;
        public static long BufferbloatThrottleRateBits { get; set; } = 0;

        /// <summary>
        /// Elevates network priority for the Hotspot without destabilizing the Windows desktop:
        /// 1. Sets process priority to High (optimal responsiveness without starving OS input/audio/DWM).
        /// 2. Optimizes Hotspot Virtual Adapter route metric (Metric = 5) for highest routing priority.
        /// 3. Binds the QoS Packet Scheduler (ms_pacer) - without it NetQoS cannot shape or tag traffic.
        /// 4. Prioritizes hotspot transit traffic (192.168.137.0/24) via NetQoS (Precedence 128, 802.1p Tag 7, DSCP 46).
        /// 5. Deprioritizes desktop browsers (Precedence 64).
        /// </summary>
        /// <param name="allowPacerRebind">
        /// When true, a disabled QoS Packet Scheduler binding may be enabled on any active adapter.
        /// Rebinding briefly resets the link, so callers should only pass true for explicit user actions
        /// (hotspot start, refresh rules, priority button). The hotspot's own virtual adapter is always bound.
        /// </param>
        /// <param name="preserveGigabitLinkSpeed">
        /// Opt-in flag for auditing/optimizing physical Ethernet adapters to prevent 10 Mbps downshifting.
        /// If null, uses the global static PreserveGigabitLinkSpeed setting.
        /// </param>
        public async Task<(bool success, string details)> EngageHighestPriorityAsync(bool allowPacerRebind = false, bool? preserveGigabitLinkSpeed = null)
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
                    bool checkPrioVlan = !_priorityVlanConfigured;
                    bool enableEthOpt = preserveGigabitLinkSpeed ?? PreserveGigabitLinkSpeed;
                    long throttleBits = (EnableAntiBufferbloat && BufferbloatThrottleRateBits > 0) ? BufferbloatThrottleRateBits : 0;
                    string script = EngageScript
                        .Replace("__ALLOW_REBIND__", allowPacerRebind ? "$true" : "$false")
                        .Replace("__CHECK_PRIO_VLAN__", checkPrioVlan ? "$true" : "$false")
                        .Replace("__BUFFERBLOAT_THROTTLE__", throttleBits > 0 ? throttleBits.ToString() : "0");
                    string output = await RunPowerShellAsync(script, 30000);
                    Debug.WriteLine($"HotspotPriorityService engage output: {output}");

                    var pacerEnabled = new List<string>();
                    var pacerMissing = new List<string>();
                    var pacerDisabled = new List<string>();
                    var nicPrioEnabled = new List<string>();
                    var failures = new List<string>();

                    bool hasNicPrioFail = false;
                    foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("PACER_ENABLED:", StringComparison.Ordinal))
                            pacerEnabled.Add(line.Substring("PACER_ENABLED:".Length).Trim());
                        else if (line.StartsWith("PACER_MISSING:", StringComparison.Ordinal))
                            pacerMissing.Add(line.Substring("PACER_MISSING:".Length).Trim());
                        else if (line.StartsWith("PACER_DISABLED:", StringComparison.Ordinal))
                            pacerDisabled.Add(line.Substring("PACER_DISABLED:".Length).Trim());
                        else if (line.StartsWith("NIC_PRIO_ENABLED:", StringComparison.Ordinal))
                            nicPrioEnabled.Add(line.Substring("NIC_PRIO_ENABLED:".Length).Trim());
                        else if (line.StartsWith("NIC_PRIO_FAIL:", StringComparison.Ordinal))
                        {
                            hasNicPrioFail = true;
                            failures.Add(line.Substring("NIC_PRIO_FAIL:".Length).Trim());
                        }
                        else if (line.StartsWith("PACER_FAIL:", StringComparison.Ordinal) ||
                                 line.StartsWith("QOS_FAIL:", StringComparison.Ordinal))
                            failures.Add(line.Substring(line.IndexOf(':') + 1).Trim());
                    }

                    // Unified Ethernet speed optimization (routed through the shared cache and concurrency lock)
                    var ethModified = new List<string>();
                    if (enableEthOpt)
                    {
                        var ethResults = await EthernetSpeedOptimizer.AuditAndOptimizeAsync(
                            null,
                            msg => Debug.WriteLine($"[Hotspot Priority] {msg}"),
                            forceRecheck: false).ConfigureAwait(false);

                        foreach (var res in ethResults)
                        {
                            if (res.Success && res.Property != "All" && res.Property != "None")
                            {
                                ethModified.Add($"{res.Adapter}: {res.Property} → {res.NewValue}");
                            }
                            else if (!res.Success && res.Property != "None")
                            {
                                failures.Add($"Ethernet {res.Adapter} ({res.Property}): {res.ErrorMessage}");
                            }
                        }
                    }

                    _isPriorityEngaged = true;
                    if (!hasNicPrioFail)
                    {
                        _priorityVlanConfigured = true;
                    }

                    var parts = new List<string>
                    {
                        "⚡ High Priority Active: High CPU priority • Upstream connection top priority (over browsers) • Route metric optimized"
                    };

                    if (pacerEnabled.Count > 0)
                        parts.Add($"QoS Packet Scheduler enabled on {string.Join(", ", pacerEnabled)} (momentary link refresh; QoS tagging active)");

                    if (nicPrioEnabled.Count > 0)
                        parts.Add($"802.1p Priority & VLAN tagging enabled on physical uplink: {string.Join(", ", nicPrioEnabled)}");

                    if (ethModified.Count > 0)
                        parts.Add($"Ethernet link optimizations verified: {string.Join(", ", ethModified)}");

                    if (pacerMissing.Count > 0)
                        parts.Add($"⚠️ Adapter(s) without a QoS Packet Scheduler driver: {string.Join(", ", pacerMissing)}");

                    if (pacerDisabled.Count > 0)
                        parts.Add($"⚠️ QoS Packet Scheduler is disabled on {string.Join(", ", pacerDisabled)} - Windows will store rate limits but not enforce them. Enable it in adapter properties (QoS Packet Scheduler) or run: Enable-NetAdapterBinding -Name '<adapter>' -ComponentID ms_pacer");

                    if (failures.Count > 0)
                    {
                        parts.Add($"⚠️ QoS enforcement notice: {string.Join("; ", failures)}");
                        return (false, string.Join(" • ", parts));
                    }

                    return (true, string.Join(" • ", parts));
                }
                catch (Exception ex)
                {
                    return (false, $"Priority optimization warning: {ex.Message}");
                }
            });
        }

        private const string EngageScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$allowRebind = __ALLOW_REBIND__
$checkPriorityVlan = __CHECK_PRIO_VLAN__
$results = New-Object System.Collections.ArrayList
function Add-Result([string]$v) { [void]$results.Add($v) }

# A. Prioritize Hotspot Virtual Adapter Route Metric (Metric = 5)
try {
    Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { ($_.InterfaceAlias -like '*Wi-Fi Direct*' -or $_.InterfaceAlias -like '*Local Area Connection*') -and $_.ConnectionState -eq 'Connected' } |
        Set-NetIPInterface -InterfaceMetric 5 -ErrorAction SilentlyContinue
} catch {}

# B. QoS Packet Scheduler (ms_pacer) must be bound to every active adapter. Without it,
#    NetQoS policies are stored but never shape traffic or apply 802.1p tags.
#    The hotspot's own virtual adapter is always safe to bind. Rebinding the upstream adapter
#    briefly resets the link, so it only happens when the caller explicitly allowed it.
foreach ($adapter in @(Get-NetAdapter -ErrorAction SilentlyContinue)) {
    if ($adapter.Status -ne 'Up') { continue }
    $bind = Get-NetAdapterBinding -Name $adapter.Name -ComponentID 'ms_pacer' -ErrorAction SilentlyContinue
    if (-not $bind) { Add-Result ""PACER_MISSING:$($adapter.Name)""; continue }
    if ($bind.Enabled) { Add-Result ""PACER_OK:$($adapter.Name)""; continue }
    $isHotspotAdapter = ($adapter.Name -like '*Local Area Connection*' -or $adapter.InterfaceDescription -like '*Wi-Fi Direct*')
    if (-not $isHotspotAdapter -and -not $allowRebind) { Add-Result ""PACER_DISABLED:$($adapter.Name)""; continue }
    try {
        Enable-NetAdapterBinding -Name $adapter.Name -ComponentID 'ms_pacer' -ErrorAction Stop
        Start-Sleep -Milliseconds 900
        $after = Get-NetAdapterBinding -Name $adapter.Name -ComponentID 'ms_pacer' -ErrorAction SilentlyContinue
        if ($after -and $after.Enabled) { Add-Result ""PACER_ENABLED:$($adapter.Name)"" }
        else { Add-Result ""PACER_FAIL:$($adapter.Name) binding did not stick"" }
    } catch {
        Add-Result ""PACER_FAIL:$($adapter.Name) $($_.Exception.Message)""
    }
}

# C. Enable 802.1p Priority & VLAN Tagging on physical uplink adapters (checked at most once per session)
if ($checkPriorityVlan) {
    try {
        $physAdapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' })
        foreach ($pa in $physAdapters) {
            $props = @(Get-NetAdapterAdvancedProperty -Name $pa.Name -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like '*Priority*' -or $_.DisplayName -like '*VLAN*' })
            foreach ($prop in $props) {
                if ($prop.DisplayValue -notlike '*Disable*' -and $prop.DisplayValue -ne 'Disabled') {
                    Add-Result ""NIC_PRIO_OK:$($pa.Name)""
                    continue
                }
                $valid = @($prop.ValidDisplayValues)
                $enableVal = $valid | Where-Object { $_ -like '*Priority*VLAN*Enable*' -or $_ -like '*Priority & VLAN*' } | Select-Object -First 1
                if (-not $enableVal) {
                    $enableVal = $valid | Where-Object { $_ -like '*Enable*' } | Select-Object -First 1
                }
                if ($enableVal) {
                    try {
                        Set-NetAdapterAdvancedProperty -Name $pa.Name -DisplayName $prop.DisplayName -DisplayValue $enableVal -ErrorAction Stop
                        Add-Result ""NIC_PRIO_ENABLED:$($pa.Name) ($enableVal)""
                    } catch {
                        Add-Result ""NIC_PRIO_FAIL:$($pa.Name) $($_.Exception.Message)""
                    }
                } else {
                    Add-Result ""NIC_PRIO_SKIP:$($pa.Name) (no matching enabled value)""
                }
            }
        }
    } catch {
        Add-Result ""NIC_PRIO_FAIL:Global $($_.Exception.Message)""
    }
}

# D. Prioritize all Hotspot Transit Traffic across the Main Shared Connection (Precedence 128).
#    Client priorities (130-240), bandwidth caps (245) and blocks (255) still take precedence.
$bufferbloatThrottle = __BUFFERBLOAT_THROTTLE__
foreach ($policy in @(
    @{ Name = 'Hotspot_Transit_Up';   Dir = 'src' },
    @{ Name = 'Hotspot_Transit_Down'; Dir = 'dst' })) {
    try {
        Remove-NetQosPolicy -Name $policy.Name -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
        if ($policy.Dir -eq 'src') {
            if ($bufferbloatThrottle -gt 0) {
                New-NetQosPolicy -Name $policy.Name -IPSrcPrefixMatchCondition '192.168.137.0/24' -PriorityValue8021Action 7 -DSCPAction 46 -Precedence 128 -ThrottleRateActionBitsPerSecond $bufferbloatThrottle -Confirm:$false -ErrorAction Stop | Out-Null
            } else {
                New-NetQosPolicy -Name $policy.Name -IPSrcPrefixMatchCondition '192.168.137.0/24' -PriorityValue8021Action 7 -DSCPAction 46 -Precedence 128 -Confirm:$false -ErrorAction Stop | Out-Null
            }
        } else {
            if ($bufferbloatThrottle -gt 0) {
                New-NetQosPolicy -Name $policy.Name -IPDstPrefixMatchCondition '192.168.137.0/24' -PriorityValue8021Action 7 -DSCPAction 46 -Precedence 128 -ThrottleRateActionBitsPerSecond $bufferbloatThrottle -Confirm:$false -ErrorAction Stop | Out-Null
            } else {
                New-NetQosPolicy -Name $policy.Name -IPDstPrefixMatchCondition '192.168.137.0/24' -PriorityValue8021Action 7 -DSCPAction 46 -Precedence 128 -Confirm:$false -ErrorAction Stop | Out-Null
            }
        }
        Get-NetQosPolicy -Name $policy.Name -ErrorAction Stop | Out-Null
    } catch {
        Add-Result ""QOS_FAIL:$($policy.Name) $($_.Exception.Message)""
    }
}

# D2. Ensure any legacy Hotspot_Gaming_FastPath policy is removed so it does not bypass the transit throttle
try {
    Remove-NetQosPolicy -Name 'Hotspot_Gaming_FastPath' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
} catch {}

# E. Keep desktop browsers below hotspot transit. -AppPathNameMatchCondition does NOT support
#    wildcards, so the plain executable name is the only form Windows can match.
$browsers = @('chrome.exe', 'msedge.exe', 'firefox.exe', 'brave.exe', 'opera.exe', 'vivaldi.exe', 'tor.exe')
foreach ($b in $browsers) {
    $name = ""Hotspot_Browser_$b""
    try {
        Remove-NetQosPolicy -Name $name -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
        New-NetQosPolicy -Name $name -AppPathNameMatchCondition $b -PriorityValue8021Action 1 -DSCPAction 8 -Precedence 64 -Confirm:$false -ErrorAction Stop | Out-Null
    } catch {
        Add-Result ""QOS_FAIL:$name $($_.Exception.Message)""
    }
}

foreach ($r in $results) { Write-Output $r }
Write-Output 'DONE'
";

        private const string CleanupScript = @"
$ErrorActionPreference = 'SilentlyContinue'
# NetQoS policies are persistent (PolicyStore = localhost), so every Hotspot_* policy must be
# deleted - stale per-IP QoS/priority/block rules would otherwise outlive the application.
$names = @(Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Hotspot_*' } | Select-Object -ExpandProperty Name)
foreach ($n in $names) {
    Remove-NetQosPolicy -Name $n -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}
Write-Output ('REMOVED:' + $names.Count)
";

        /// <summary>
        /// Releases priority and removes all NetQoS policies when the Hotspot stops.
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

                try
                {
                    _ = RunPowerShellAsync(CleanupScript, 15000);
                }
                catch { }

                _isPriorityEngaged = false;
            }
            catch { }
        }

        /// <summary>
        /// Synchronous best-effort cleanup for process exit. NetQoS policies are persistent,
        /// so they must be removed even when the app closes without stopping the hotspot.
        /// </summary>
        public static void CleanupAllPolicies()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                proc.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch { }

            try
            {
                // Run on the thread pool: this is called from the WPF UI thread during OnExit, and
                // blocking there while an await needs that same Dispatcher would deadlock shutdown.
                string output = Task.Run(() => RunPowerShellAsync(CleanupScript, 15000)).GetAwaiter().GetResult();
                Debug.WriteLine($"HotspotPriorityService cleanup: {output}");
            }
            catch { }

            _isPriorityEngaged = false;
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
                await proc.StandardInput.WriteLineAsync(script).ConfigureAwait(false);
                proc.StandardInput.Close();

                // Concurrently read stdout and stderr to prevent deadlocks
                var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var errTask = proc.StandardError.ReadToEndAsync(cts.Token);

                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                string output = await outTask.ConfigureAwait(false);
                string error = await errTask.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error))
                    Debug.WriteLine($"HotspotPriorityService PowerShell stderr: {error.Trim()}");
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
