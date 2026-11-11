using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WifiAnalyzerHotspot.Services
{
    public class EthernetOptimizationResult
    {
        public string Adapter { get; set; } = string.Empty;
        public string Property { get; set; } = string.Empty;
        public string OldValue { get; set; } = string.Empty;
        public string NewValue { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Consolidated, single source of truth for physical Ethernet link speed optimization.
    /// Permanently disarms vendor power-saving throttles (Realtek WOL &amp; Shutdown Link Speed: 10 Mbps First,
    /// Green Ethernet, Energy-Efficient Ethernet, Advanced EEE, Power Saving Mode, Gigabit Lite, Auto Disable Gigabit)
    /// and ensures Speed &amp; Duplex is set to Auto Negotiation.
    /// 
    /// Features:
    /// - Read-back verification: Confirms with the driver that property changes were actually committed.
    /// - Pipe-delimited parsing ('|'): Prevents truncation with colons in adapter or property names.
    /// - Session caching (_auditedAdapters): Deduplicates audit runs so subsequent drop recoveries on HDD take 0ms.
    /// - Shared script generation: Eliminates code drift between Hotspot Priority and Active Reconnect.
    /// </summary>
    public static class EthernetSpeedOptimizer
    {
        private static readonly ConcurrentDictionary<string, DateTime> _auditedAdapters = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Clears the session audit cache for an adapter or all adapters, forcing the next check to re-audit.
        /// </summary>
        public static void InvalidateAuditCache(string? adapterName = null)
        {
            if (string.IsNullOrWhiteSpace(adapterName))
            {
                _auditedAdapters.Clear();
            }
            else
            {
                _auditedAdapters.TryRemove(adapterName.Trim(), out _);
            }
        }

        /// <summary>
        /// Generates the unified PowerShell script snippet for auditing and optimizing Ethernet adapters.
        /// </summary>
        /// <param name="targetAdapter">Optional specific adapter name. If null, targets all physical Ethernet adapters.</param>
        /// <param name="isSnippet">True when embedded inside another PowerShell script (e.g. HotspotPriorityService).</param>
        public static string GetPowerShellScript(string? targetAdapter = null, bool isSnippet = false)
        {
            string escapedTarget = (targetAdapter ?? string.Empty).Replace("'", "''");

            string helperDef = isSnippet ? string.Empty : @"
function Add-Result {
    param([string]$msg)
    $script:results += $msg
}
$script:results = @()
";

            string returnBlock = isSnippet ? string.Empty : @"
foreach ($r in $script:results) { Write-Output $r }
";

            return $@"
{helperDef}
# ── Ethernet Link Speed & Power-Throttle Optimization ──
$targetName = '{escapedTarget}'
$ethAdapters = if ($targetName) {{
    @(Get-NetAdapter -Name $targetName -Physical -ErrorAction SilentlyContinue)
}} else {{
    @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object {{
        $_.InterfaceDescription -like '*Ethernet*' -or
        $_.InterfaceDescription -like '*GbE*' -or
        $_.InterfaceDescription -like '*Gigabit*' -or
        $_.InterfaceDescription -like '*Realtek*' -or
        $_.InterfaceDescription -like '*Intel*'
    }})
}}
if (-not $ethAdapters -or $ethAdapters.Count -eq 0) {{
    $ethAdapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object {{ $_.MediaType -notlike '*Native 802.11*' }})
}}

foreach ($ea in $ethAdapters) {{
    try {{
        $anyModified = $false

        # 1. Ensure Speed & Duplex is Auto Negotiation (not forced down to 10 Mbps)
        $spd = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName '*Speed*Duplex*' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($spd -and $spd.DisplayValue -ne 'Auto Negotiation' -and $spd.DisplayValue -like '*10 Mbps*') {{
            $oldVal = $spd.DisplayValue
            Set-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $spd.DisplayName -DisplayValue 'Auto Negotiation' -ErrorAction SilentlyContinue
            $verifySpd = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $spd.DisplayName -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($verifySpd -and $verifySpd.DisplayValue -eq 'Auto Negotiation') {{
                Add-Result ""ETH_MODIFIED|$($ea.Name)|$($spd.DisplayName)|$oldVal|Auto Negotiation""
                $anyModified = $true
            }} else {{
                Add-Result ""ETH_FAIL|$($ea.Name)|$($spd.DisplayName)|Auto Negotiation|Driver rejected or failed to persist change""
            }}
        }}

        # 2. Prevent link downshifting to 10 Mbps on reset/sleep (Realtek WOL & Shutdown Link Speed: 10 Mbps First)
        $wol = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName '*WOL*Shutdown*Link*Speed*' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($wol -and $wol.DisplayValue -eq '10 Mbps First') {{
            $oldVal = $wol.DisplayValue
            $targetWol = @($wol.ValidDisplayValues) | Where-Object {{ $_ -eq 'Not Speed Down' -or $_ -eq 'Disabled' }} | Select-Object -First 1
            if ($targetWol) {{
                Set-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $wol.DisplayName -DisplayValue $targetWol -ErrorAction SilentlyContinue
                $verifyWol = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $wol.DisplayName -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($verifyWol -and $verifyWol.DisplayValue -eq $targetWol) {{
                    Add-Result ""ETH_MODIFIED|$($ea.Name)|$($wol.DisplayName)|$oldVal|$targetWol""
                    $anyModified = $true
                }} else {{
                    Add-Result ""ETH_FAIL|$($ea.Name)|$($wol.DisplayName)|$targetWol|Driver rejected or failed to persist change""
                }}
            }}
        }}

        # 3. Disable energy-saving throttles (Green Ethernet, Energy-Efficient Ethernet, Advanced EEE, Power Saving Mode, Gigabit Lite, Auto Disable Gigabit)
        $pwrPatterns = @('*Green*Ethernet*', '*Energy*Efficient*Ethernet*', '*Advanced*EEE*', '*Power*Saving*Mode*', '*Gigabit*Lite*', '*Auto*Disable*Gigabit*')
        foreach ($pwrName in $pwrPatterns) {{
            $pwr = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $pwrName -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($pwr -and ($pwr.DisplayValue -eq 'Enabled' -or $pwr.DisplayValue -like '*Enable*')) {{
                $oldVal = $pwr.DisplayValue
                $targetDis = @($pwr.ValidDisplayValues) | Where-Object {{ $_ -eq 'Disabled' -or $_ -like '*Disable*' }} | Select-Object -First 1
                if ($targetDis) {{
                    Set-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $pwr.DisplayName -DisplayValue $targetDis -ErrorAction SilentlyContinue
                    $verifyPwr = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $pwr.DisplayName -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($verifyPwr -and ($verifyPwr.DisplayValue -eq $targetDis -or $verifyPwr.DisplayValue -like '*Disable*')) {{
                        Add-Result ""ETH_MODIFIED|$($ea.Name)|$($pwr.DisplayName)|$oldVal|$targetDis""
                        $anyModified = $true
                    }} else {{
                        Add-Result ""ETH_FAIL|$($ea.Name)|$($pwr.DisplayName)|$targetDis|Driver rejected or failed to persist change""
                    }}
                }}
            }}
        }}

        if (-not $anyModified) {{
            Add-Result ""ETH_VERIFIED|$($ea.Name)|All settings optimal""
        }}
    }} catch {{
        Add-Result ""ETH_FAIL|$($ea.Name)|Global|Error|$($_.Exception.Message)""
    }}
}}
{returnBlock}
";
        }

        /// <summary>
        /// Parses the pipe-delimited output from the PowerShell optimization script into structured results.
        /// </summary>
        public static List<EthernetOptimizationResult> ParseResults(string output)
        {
            var list = new List<EthernetOptimizationResult>();
            if (string.IsNullOrWhiteSpace(output)) return list;

            foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("ETH_MODIFIED|", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 5)
                    {
                        list.Add(new EthernetOptimizationResult
                        {
                            Adapter = parts[1].Trim(),
                            Property = parts[2].Trim(),
                            OldValue = parts[3].Trim(),
                            NewValue = parts[4].Trim(),
                            Success = true
                        });
                    }
                }
                else if (line.StartsWith("ETH_FAIL|", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 5)
                    {
                        list.Add(new EthernetOptimizationResult
                        {
                            Adapter = parts[1].Trim(),
                            Property = parts[2].Trim(),
                            NewValue = parts[3].Trim(),
                            Success = false,
                            ErrorMessage = parts[4].Trim()
                        });
                    }
                }
                else if (line.StartsWith("ETH_VERIFIED|", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 2)
                    {
                        list.Add(new EthernetOptimizationResult
                        {
                            Adapter = parts[1].Trim(),
                            Property = "All",
                            Success = true,
                            NewValue = "Optimal"
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Audits and optimizes the given adapter (or all physical Ethernet adapters).
        /// Respects the session cache so subsequent recoveries on the same adapter are instant (0ms).
        /// Logs verified changes, errors, and status into onLog callback.
        /// </summary>
        public static async Task<List<EthernetOptimizationResult>> AuditAndOptimizeAsync(
            string? adapterName,
            Action<string>? onLog,
            bool forceRecheck = false,
            CancellationToken token = default)
        {
            string cleanAdapter = (adapterName ?? string.Empty).Trim();

            // Session caching check: avoid running cold PowerShell processes if already verified
            if (!forceRecheck && !string.IsNullOrEmpty(cleanAdapter) && _auditedAdapters.ContainsKey(cleanAdapter))
            {
                return new List<EthernetOptimizationResult>();
            }

            string script = GetPowerShellScript(cleanAdapter, isSnippet: false);
            string output = await RunPowerShellAsync(script, 15000, token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(output))
            {
                onLog?.Invoke("[ANTI-10M] Ethernet link speed audit timed out or failed to execute PowerShell.");
                return new List<EthernetOptimizationResult>();
            }

            var results = ParseResults(output);
            foreach (var res in results)
            {
                if (res.Success)
                {
                    if (res.Property == "All")
                    {
                        if (forceRecheck)
                        {
                            onLog?.Invoke($"[ANTI-10M] Verified: Adapter '{res.Adapter}' is already operating with full Gigabit parameters.");
                        }
                    }
                    else
                    {
                        onLog?.Invoke($"[ANTI-10M] Verified: Set '{res.Property}' from '{res.OldValue}' to '{res.NewValue}' on '{res.Adapter}'.");
                    }
                }
                else
                {
                    onLog?.Invoke($"[ANTI-10M] ⚠️ Failed to configure '{res.Property}' on '{res.Adapter}': {res.ErrorMessage}");
                }

                if (!string.IsNullOrEmpty(res.Adapter))
                {
                    _auditedAdapters[res.Adapter] = DateTime.UtcNow;
                }
            }

            if (!string.IsNullOrEmpty(cleanAdapter))
            {
                _auditedAdapters[cleanAdapter] = DateTime.UtcNow;
            }

            return results;
        }

        private static async Task<string> RunPowerShellAsync(string script, int timeoutMs = 15000, CancellationToken token = default)
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

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                linkedCts.CancelAfter(timeoutMs);

                await proc.StandardInput.WriteLineAsync(script).ConfigureAwait(false);
                proc.StandardInput.Close();

                var outTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
                var errTask = proc.StandardError.ReadToEndAsync(linkedCts.Token);

                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                string output = await outTask.ConfigureAwait(false);
                _ = await errTask.ConfigureAwait(false);
                return output.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
