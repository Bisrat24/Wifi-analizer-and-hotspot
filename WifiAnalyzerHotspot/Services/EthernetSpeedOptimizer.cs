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
    /// - Read-back verification with retry: Confirms asynchronous driver persistence (up to 3 retries with no trailing sleep).
    /// - Strict media filter: Excludes Wi-Fi radios (Native 802.11 / Wireless) so only wired 802.3 PHYs are audited.
    /// - Distinguishes unverified adapters: Emits ETH_SKIPPED if no recognized properties were found (not cached).
    /// - Time-to-Live Session caching: 30-minute TTL with failure exclusion (failed properties never cached as audited).
    /// - Self-contained power resume invalidation: Clears cache when Windows wakes from sleep (PowerModes.Resume).
    /// - Reversible driver defaults: Captures and persists original driver values with RestoreDefaultsAsync.
    /// - Concurrency serialization: SemaphoreSlim prevents racing watchdog/manual/hotspot audits.
    /// </summary>
    public static class EthernetSpeedOptimizer
    {
        private static readonly ConcurrentDictionary<string, DateTime> _auditedAdapters = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> _originalValuesBackup = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _auditLock = new(1, 1);
        public static readonly TimeSpan AuditTtl = TimeSpan.FromMinutes(30);

        static EthernetSpeedOptimizer()
        {
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
                {
                    if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                    {
                        // Link speed or power profiles may downshift after waking from sleep; clear cache to force audit
                        InvalidateAuditCache();
                    }
                };
            }
            catch { }
        }

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
        /// Checks if an adapter was already audited within the TTL window.
        /// </summary>
        public static bool IsAdapterAudited(string? adapterName)
        {
            if (string.IsNullOrWhiteSpace(adapterName)) return false;
            string key = adapterName.Trim();
            if (_auditedAdapters.TryGetValue(key, out var timestamp))
            {
                if (DateTime.UtcNow - timestamp < AuditTtl)
                {
                    return true;
                }
                _auditedAdapters.TryRemove(key, out _);
            }
            return false;
        }

        /// <summary>
        /// Gets the stored original driver values before optimization.
        /// Key: "AdapterName|PropertyDisplayName", Value: OriginalValue
        /// </summary>
        public static IReadOnlyDictionary<string, string> GetOriginalValuesBackup() => _originalValuesBackup;

        /// <summary>
        /// Loads previously persisted original property backups into memory.
        /// </summary>
        public static void LoadOriginalValuesBackup(IDictionary<string, string>? backups)
        {
            if (backups == null) return;
            foreach (var kvp in backups)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && kvp.Value != null)
                {
                    _originalValuesBackup.TryAdd(kvp.Key, kvp.Value);
                }
            }
        }

        /// <summary>
        /// Records the original value of a property if not already recorded.
        /// </summary>
        public static void RecordOriginalValue(string adapter, string property, string oldValue)
        {
            if (string.IsNullOrEmpty(adapter) || string.IsNullOrEmpty(property)) return;
            string key = $"{adapter}|{property}";
            _originalValuesBackup.TryAdd(key, oldValue);
        }

        /// <summary>
        /// Registers successfully audited adapters into the session cache.
        /// Adapters with failed properties or adapters that skipped all properties are excluded from caching.
        /// </summary>
        public static void RegisterAuditedAdapters(IEnumerable<EthernetOptimizationResult> results)
        {
            if (results == null) return;
            var uncacheableAdapters = new HashSet<string>(
                results.Where(r => (!r.Success && r.Property != "None") || r.Property == "None")
                       .Select(r => r.Adapter),
                StringComparer.OrdinalIgnoreCase);

            foreach (var r in results)
            {
                if (r.Success && !string.IsNullOrEmpty(r.Adapter) && !uncacheableAdapters.Contains(r.Adapter))
                {
                    _auditedAdapters[r.Adapter] = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// Generates the unified PowerShell script snippet for auditing and optimizing Ethernet adapters.
        /// </summary>
        /// <param name="targetAdapter">Optional specific adapter name. If null, targets all physical Ethernet adapters.</param>
        /// <param name="isSnippet">True when embedded inside another PowerShell script.</param>
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
    @(Get-NetAdapter -Name $targetName -Physical -ErrorAction SilentlyContinue | Where-Object {{
        $_.MediaType -notlike '*Native 802.11*' -and $_.MediaType -notlike '*Wireless*'
    }})
}} else {{
    @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object {{
        ($_.InterfaceDescription -like '*Ethernet*' -or
         $_.InterfaceDescription -like '*GbE*' -or
         $_.InterfaceDescription -like '*Gigabit*' -or
         $_.InterfaceDescription -like '*Realtek*' -or
         $_.InterfaceDescription -like '*Intel*') -and
        $_.MediaType -notlike '*Native 802.11*' -and
        $_.MediaType -notlike '*Wireless*'
    }})
}}
if (-not $ethAdapters -or $ethAdapters.Count -eq 0) {{
    $ethAdapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object {{
        $_.MediaType -like '*802.3*' -and
        $_.MediaType -notlike '*Native 802.11*' -and
        $_.MediaType -notlike '*Wireless*'
    }})
}}

foreach ($ea in $ethAdapters) {{
    try {{
        $anyModified = $false
        $foundAnyProperty = $false

        # 1. Ensure Speed & Duplex is Auto Negotiation (not forced down to 10 Mbps)
        $spd = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName '*Speed*Duplex*' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($spd) {{
            $foundAnyProperty = $true
            if ($spd.DisplayValue -ne 'Auto Negotiation' -and $spd.DisplayValue -like '*10 Mbps*') {{
                $oldVal = $spd.DisplayValue
                Set-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $spd.DisplayName -DisplayValue 'Auto Negotiation' -ErrorAction SilentlyContinue
                
                # Asynchronous driver commit retry loop (up to 3 attempts, no sleep on last failure)
                $verifySpd = $null
                for ($retry = 0; $retry -lt 3; $retry++) {{
                    $verifySpd = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $spd.DisplayName -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($verifySpd -and $verifySpd.DisplayValue -eq 'Auto Negotiation') {{ break }}
                    if ($retry -lt 2) {{ Start-Sleep -Milliseconds 150 }}
                }}

                if ($verifySpd -and $verifySpd.DisplayValue -eq 'Auto Negotiation') {{
                    Add-Result ""ETH_MODIFIED|$($ea.Name)|$($spd.DisplayName)|$oldVal|Auto Negotiation""
                    $anyModified = $true
                }} else {{
                    Add-Result ""ETH_FAIL|$($ea.Name)|$($spd.DisplayName)|Auto Negotiation|Driver rejected or failed to persist change""
                }}
            }}
        }}

        # 2. Prevent link downshifting to 10 Mbps on reset/sleep (Realtek WOL & Shutdown Link Speed: 10 Mbps First)
        $wol = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName '*WOL*Shutdown*Link*Speed*' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($wol) {{
            $foundAnyProperty = $true
            if ($wol.DisplayValue -eq '10 Mbps First') {{
                $oldVal = $wol.DisplayValue
                $targetWol = @($wol.ValidDisplayValues) | Where-Object {{ $_ -eq 'Not Speed Down' -or $_ -eq 'Disabled' }} | Select-Object -First 1
                if ($targetWol) {{
                    Set-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $wol.DisplayName -DisplayValue $targetWol -ErrorAction SilentlyContinue
                    
                    $verifyWol = $null
                    for ($retry = 0; $retry -lt 3; $retry++) {{
                        $verifyWol = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $wol.DisplayName -ErrorAction SilentlyContinue | Select-Object -First 1
                        if ($verifyWol -and $verifyWol.DisplayValue -eq $targetWol) {{ break }}
                        if ($retry -lt 2) {{ Start-Sleep -Milliseconds 150 }}
                    }}

                    if ($verifyWol -and $verifyWol.DisplayValue -eq $targetWol) {{
                        Add-Result ""ETH_MODIFIED|$($ea.Name)|$($wol.DisplayName)|$oldVal|$targetWol""
                        $anyModified = $true
                    }} else {{
                        Add-Result ""ETH_FAIL|$($ea.Name)|$($wol.DisplayName)|$targetWol|Driver rejected or failed to persist change""
                    }}
                }}
            }}
        }}

        # 3. Disable energy-saving throttles (Green Ethernet, Energy-Efficient Ethernet, Advanced EEE, Power Saving Mode, Gigabit Lite, Auto Disable Gigabit)
        $pwrPatterns = @('*Green*Ethernet*', '*Energy*Efficient*Ethernet*', '*Advanced*EEE*', '*Power*Saving*Mode*', '*Gigabit*Lite*', '*Auto*Disable*Gigabit*')
        foreach ($pwrName in $pwrPatterns) {{
            $pwr = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $pwrName -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($pwr) {{
                $foundAnyProperty = $true
                if ($pwr.DisplayValue -eq 'Enabled' -or $pwr.DisplayValue -like '*Enable*') {{
                    $oldVal = $pwr.DisplayValue
                    $targetDis = @($pwr.ValidDisplayValues) | Where-Object {{ $_ -eq 'Disabled' -or $_ -like '*Disable*' }} | Select-Object -First 1
                    if ($targetDis) {{
                        Set-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $pwr.DisplayName -DisplayValue $targetDis -ErrorAction SilentlyContinue
                        
                        $verifyPwr = $null
                        for ($retry = 0; $retry -lt 3; $retry++) {{
                            $verifyPwr = Get-NetAdapterAdvancedProperty -Name $ea.Name -DisplayName $pwr.DisplayName -ErrorAction SilentlyContinue | Select-Object -First 1
                            if ($verifyPwr -and ($verifyPwr.DisplayValue -eq $targetDis -or $verifyPwr.DisplayValue -like '*Disable*')) {{ break }}
                            if ($retry -lt 2) {{ Start-Sleep -Milliseconds 150 }}
                        }}

                        if ($verifyPwr -and ($verifyPwr.DisplayValue -eq $targetDis -or $verifyPwr.DisplayValue -like '*Disable*')) {{
                            Add-Result ""ETH_MODIFIED|$($ea.Name)|$($pwr.DisplayName)|$oldVal|$targetDis""
                            $anyModified = $true
                        }} else {{
                            Add-Result ""ETH_FAIL|$($ea.Name)|$($pwr.DisplayName)|$targetDis|Driver rejected or failed to persist change""
                        }}
                    }}
                }}
            }}
        }}

        if ($anyModified) {{
            # Modifications recorded
        }} elseif ($foundAnyProperty) {{
            Add-Result ""ETH_VERIFIED|$($ea.Name)|All settings optimal""
        }} else {{
            Add-Result ""ETH_SKIPPED|$($ea.Name)|No recognized power or speed properties""
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
        /// Uses count 5 to preserve any pipes present in trailing error messages.
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
                    var parts = line.Split('|', 5);
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
                    var parts = line.Split('|', 5);
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
                    var parts = line.Split('|', 3);
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
                else if (line.StartsWith("ETH_SKIPPED|", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('|', 3);
                    if (parts.Length >= 2)
                    {
                        list.Add(new EthernetOptimizationResult
                        {
                            Adapter = parts[1].Trim(),
                            Property = "None",
                            Success = false,
                            ErrorMessage = parts.Length > 2 ? parts[2].Trim() : "No recognized properties"
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Audits and optimizes the given adapter (or all physical Ethernet adapters).
        /// Respects the session cache so subsequent recoveries on the same adapter are instant (0ms).
        /// Synchronized via SemaphoreSlim to prevent concurrent audit processes.
        /// </summary>
        public static async Task<List<EthernetOptimizationResult>> AuditAndOptimizeAsync(
            string? adapterName,
            Action<string>? onLog,
            bool forceRecheck = false,
            CancellationToken token = default)
        {
            string cleanAdapter = (adapterName ?? string.Empty).Trim();

            // Quick check outside lock
            if (!forceRecheck && !string.IsNullOrEmpty(cleanAdapter) && IsAdapterAudited(cleanAdapter))
            {
                return new List<EthernetOptimizationResult>();
            }

            await _auditLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Double-check inside lock
                if (!forceRecheck && !string.IsNullOrEmpty(cleanAdapter) && IsAdapterAudited(cleanAdapter))
                {
                    return new List<EthernetOptimizationResult>();
                }

                string script = GetPowerShellScript(cleanAdapter, isSnippet: false);
                string output = await RunPowerShellAsync(script, 20000, token).ConfigureAwait(false);

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
                            // Save original value for reversible restore
                            RecordOriginalValue(res.Adapter, res.Property, res.OldValue);
                            onLog?.Invoke($"[ANTI-10M] Verified: Set '{res.Property}' from '{res.OldValue}' to '{res.NewValue}' on '{res.Adapter}'.");
                        }
                    }
                    else if (res.Property == "None")
                    {
                        if (forceRecheck)
                        {
                            onLog?.Invoke($"[ANTI-10M] Adapter '{res.Adapter}' has no recognized vendor power/speed properties (skipped).");
                        }
                    }
                    else
                    {
                        onLog?.Invoke($"[ANTI-10M] ⚠️ Failed to configure '{res.Property}' on '{res.Adapter}': {res.ErrorMessage}");
                    }
                }

                // Unified cache registration: excludes failed or skipped adapters
                RegisterAuditedAdapters(results);

                return results;
            }
            finally
            {
                _auditLock.Release();
            }
        }

        /// <summary>
        /// Restores all previously backed up original driver property values when the user disables the toggle
        /// or clicks Restore Defaults.
        /// </summary>
        public static async Task<List<EthernetOptimizationResult>> RestoreDefaultsAsync(
            Action<string>? onLog,
            CancellationToken token = default)
        {
            if (_originalValuesBackup.IsEmpty)
            {
                onLog?.Invoke("[ANTI-10M] No modified driver properties to restore (driver settings unchanged).");
                return new List<EthernetOptimizationResult>();
            }

            await _auditLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var snapshot = _originalValuesBackup.ToArray();
                var scriptLines = new List<string>
                {
                    "function Add-Result { param([string]$msg) $script:results += $msg }",
                    "$script:results = @()"
                };

                foreach (var item in snapshot)
                {
                    var parts = item.Key.Split('|', 2);
                    if (parts.Length < 2) continue;
                    string adapter = parts[0].Replace("'", "''");
                    string property = parts[1].Replace("'", "''");
                    string originalValue = item.Value.Replace("'", "''");

                    scriptLines.Add($@"
try {{
    Set-NetAdapterAdvancedProperty -Name '{adapter}' -DisplayName '{property}' -DisplayValue '{originalValue}' -ErrorAction SilentlyContinue
    $verify = Get-NetAdapterAdvancedProperty -Name '{adapter}' -DisplayName '{property}' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($verify -and $verify.DisplayValue -eq '{originalValue}') {{
        Add-Result 'ETH_MODIFIED|{adapter}|{property}|Current|{originalValue}'
    }} else {{
        Add-Result 'ETH_FAIL|{adapter}|{property}|{originalValue}|Driver rejected restoring default'
    }}
}} catch {{
    Add-Result 'ETH_FAIL|{adapter}|{property}|{originalValue}|$($_.Exception.Message)'
}}");
                }

                scriptLines.Add("foreach ($r in $script:results) { Write-Output $r }");
                string script = string.Join("\n", scriptLines);

                string output = await RunPowerShellAsync(script, 20000, token).ConfigureAwait(false);
                var results = ParseResults(output);

                foreach (var res in results)
                {
                    if (res.Success)
                    {
                        onLog?.Invoke($"[ANTI-10M] Restored driver default: '{res.Property}' → '{res.NewValue}' on '{res.Adapter}'.");
                        _originalValuesBackup.TryRemove($"{res.Adapter}|{res.Property}", out _);
                    }
                    else
                    {
                        onLog?.Invoke($"[ANTI-10M] ⚠️ Failed to restore '{res.Property}' on '{res.Adapter}': {res.ErrorMessage}");
                    }
                }

                // Invalidate session cache after restoring defaults
                InvalidateAuditCache();

                return results;
            }
            finally
            {
                _auditLock.Release();
            }
        }

        private static async Task<string> RunPowerShellAsync(string script, int timeoutMs = 20000, CancellationToken token = default)
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
