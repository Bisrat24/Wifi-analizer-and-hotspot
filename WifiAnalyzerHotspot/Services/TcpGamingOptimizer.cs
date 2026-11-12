using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Zero-Jitter TCP Stack Optimizer for Gaming & Real-Time Traffic.
    /// Eliminates delayed ACKs and Nagle buffering on the Hotspot and Upstream interfaces:
    /// - TcpAckFrequency = 1 (Acknowledge every packet immediately, no 200ms batch delay)
    /// - TCPNoDelay = 1 (Disable Nagle algorithm for instant 64-byte game update packets)
    /// - TcpDelAckTicks = 0 (Eliminates delayed ACK timer ticks)
    /// - ECNCapability = Enabled (Explicit Congestion Notification to avoid dropped-packet ping spikes)
    /// Completely reversible with original values backup.
    /// </summary>
    public static class TcpGamingOptimizer
    {
        private const string TcpipInterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        private static readonly ConcurrentDictionary<string, string> _originalTcpBackup = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _lock = new(1, 1);

        public static IReadOnlyDictionary<string, string> GetOriginalTcpBackup() => _originalTcpBackup;

        public static void LoadOriginalTcpBackup(IDictionary<string, string>? backup)
        {
            if (backup == null) return;
            foreach (var kvp in backup)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && kvp.Value != null)
                {
                    _originalTcpBackup.TryAdd(kvp.Key, kvp.Value);
                }
            }
        }

        public static async Task<(bool success, string message)> ApplyGamingTuningAsync(
            Action<string>? onLog = null,
            CancellationToken token = default)
        {
            await _lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                onLog?.Invoke("[TCP-Tuning] Engaging Zero-Jitter Engine for Gaming & Real-Time Traffic...");

                var targetGuids = GetTargetInterfaceGuids();
                if (targetGuids.Count == 0)
                {
                    return (false, "No active network interfaces found to tune.");
                }

                // 0. Backup original ECN state if not already captured
                if (!_originalTcpBackup.ContainsKey("__GLOBAL__|ECNCapability"))
                {
                    string currentEcn = await QueryCurrentEcnCapabilityAsync(token).ConfigureAwait(false);
                    _originalTcpBackup["__GLOBAL__|ECNCapability"] = currentEcn;
                }

                int modifiedCount = 0;
                foreach (string guid in targetGuids)
                {
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey($@"{TcpipInterfacesKey}\{guid}", writable: true);
                        if (key == null) continue;

                        // 1. TcpAckFrequency
                        BackupAndSetDword(key, guid, "TcpAckFrequency", 1, ref modifiedCount, onLog);

                        // 2. TCPNoDelay
                        BackupAndSetDword(key, guid, "TCPNoDelay", 1, ref modifiedCount, onLog);

                        // 3. TcpDelAckTicks
                        BackupAndSetDword(key, guid, "TcpDelAckTicks", 0, ref modifiedCount, onLog);
                    }
                    catch (Exception ex)
                    {
                        onLog?.Invoke($"[TCP-Tuning] ⚠️ Interface {guid} tuning error: {ex.Message}");
                    }
                }

                // Enable ECN capability via netsh
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "netsh.exe",
                        Arguments = "int tcp set global ecncapability=enabled",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync(token).ConfigureAwait(false);
                    }
                    string verifiedEcn = await QueryCurrentEcnCapabilityAsync(token).ConfigureAwait(false);
                    if (verifiedEcn == "enabled")
                    {
                        onLog?.Invoke("[TCP-Tuning] ECN (Explicit Congestion Notification) set to Enabled (verified).");
                    }
                    else
                    {
                        onLog?.Invoke($"[TCP-Tuning] Notice: ECN set returned status: {verifiedEcn}");
                    }
                }
                catch { }

                onLog?.Invoke($"[TCP-Tuning] ✓ Zero-Jitter Engine engaged ({modifiedCount} properties optimized across {targetGuids.Count} interface(s)).");
                return (true, $"Zero-Jitter Engine engaged ({modifiedCount} parameters tuned)");
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[TCP-Tuning] Failed: {ex.Message}");
                return (false, ex.Message);
            }
            finally
            {
                _lock.Release();
            }
        }

        public static async Task<(bool success, string message)> RestoreDefaultsAsync(
            Action<string>? onLog = null,
            CancellationToken token = default)
        {
            await _lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                onLog?.Invoke("[TCP-Tuning] Restoring default Windows TCP stack parameters...");

                int restoredCount = 0;
                var snapshot = _originalTcpBackup.ToArray();

                foreach (var item in snapshot)
                {
                    var parts = item.Key.Split('|', 2);
                    if (parts.Length < 2) continue;
                    string guid = parts[0];
                    string paramName = parts[1];
                    string originalVal = item.Value;

                    if (guid.Equals("__GLOBAL__", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey($@"{TcpipInterfacesKey}\{guid}", writable: true);
                        if (key != null)
                        {
                            if (originalVal == "__MISSING__")
                            {
                                key.DeleteValue(paramName, throwOnMissingValue: false);
                            }
                            else if (int.TryParse(originalVal, out int dwordVal))
                            {
                                key.SetValue(paramName, dwordVal, RegistryValueKind.DWord);
                            }
                            restoredCount++;
                            _originalTcpBackup.TryRemove(item.Key, out _);
                        }
                    }
                    catch (Exception ex)
                    {
                        onLog?.Invoke($"[TCP-Tuning] ⚠️ Failed to restore {paramName} on {guid}: {ex.Message}");
                    }
                }

                // Restore ECN capability only if an original state was backed up
                if (_originalTcpBackup.TryGetValue("__GLOBAL__|ECNCapability", out var savedEcn) &&
                    !string.IsNullOrEmpty(savedEcn) && savedEcn != "__MISSING__")
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "netsh.exe",
                            Arguments = $"int tcp set global ecncapability={savedEcn}",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true
                        };
                        using var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            await proc.WaitForExitAsync(token).ConfigureAwait(false);
                        }
                        onLog?.Invoke($"[TCP-Tuning] ECN capability restored to '{savedEcn}'.");
                    }
                    catch { }
                    finally
                    {
                        _originalTcpBackup.TryRemove("__GLOBAL__|ECNCapability", out _);
                    }
                }

                onLog?.Invoke($"[TCP-Tuning] ✓ Restored default TCP stack configuration ({restoredCount} parameters reverted).");
                return (true, $"Restored {restoredCount} default TCP parameters");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _lock.Release();
            }
        }

        private static void BackupAndSetDword(RegistryKey key, string guid, string paramName, int targetValue, ref int modifiedCount, Action<string>? onLog)
        {
            string backupKey = $"{guid}|{paramName}";
            object? existing = key.GetValue(paramName);

            if (!_originalTcpBackup.ContainsKey(backupKey))
            {
                _originalTcpBackup[backupKey] = existing != null ? existing.ToString() ?? "0" : "__MISSING__";
            }

            int currentVal = existing is int i ? i : -1;
            if (currentVal != targetValue)
            {
                key.SetValue(paramName, targetValue, RegistryValueKind.DWord);
                // Read-back verification
                object? readBack = key.GetValue(paramName);
                if (readBack is int verified && verified == targetValue)
                {
                    modifiedCount++;
                    onLog?.Invoke($"[TCP-Tuning] Set {paramName} = {targetValue} on {{{guid}}} (verified)");
                }
                else
                {
                    onLog?.Invoke($"[TCP-Tuning] ⚠️ Failed to verify {paramName} = {targetValue} on {{{guid}}}");
                }
            }
        }

        private static async Task<string> QueryCurrentEcnCapabilityAsync(CancellationToken token = default)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "int tcp show global",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    await proc.WaitForExitAsync(token).ConfigureAwait(false);
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains("ECN", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split(':');
                            if (parts.Length > 1)
                            {
                                string val = parts[1].Trim().ToLowerInvariant();
                                if (val.Contains("enabled")) return "enabled";
                                if (val.Contains("disabled")) return "disabled";
                                if (val.Contains("default")) return "default";
                            }
                        }
                    }
                }
            }
            catch { }
            return "disabled";
        }

        private static List<string> GetTargetInterfaceGuids()
        {
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in nics)
                {
                    // Target virtual ICS hotspot adapter and active Wi-Fi/Ethernet adapters
                    if (nic.OperationalStatus == OperationalStatus.Up ||
                        nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        nic.Description.Contains("Direct", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = nic.Id.Trim('{', '}');
                        guids.Add(id);
                    }
                }
            }
            catch { }

            return new List<string>(guids);
        }
    }
}
