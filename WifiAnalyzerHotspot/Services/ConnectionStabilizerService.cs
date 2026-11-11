using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class ConnectionStabilizerService
    {
        private CancellationTokenSource? _keepAliveCts;
        private CancellationTokenSource? _reconnectCts;

        private static readonly Dictionary<string, (int Attempts, DateTime LastAttempt)> _adapterResetHistory = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _adapterResetLock = new();

        private static readonly HttpClient _probeClient = new(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            AllowAutoRedirect = false
        })
        { Timeout = TimeSpan.FromSeconds(3) };

        public static string CaptivePortalProbeUrl { get; set; } = "http://www.msftconnecttest.com/connecttest.txt";

        // Rotating ping targets — if one is blocked by ISP we fail-over
        private static readonly string[] _pingTargets = { "1.1.1.1", "8.8.8.8", "9.9.9.9", "208.67.222.222" };

        // ═══════════════════════════════════════════════════════════════════
        //  ACTIVE KEEP-ALIVE
        // ═══════════════════════════════════════════════════════════════════
        public void StartKeepAlive(ConnectionStabilizerModel model)
        {
            StopKeepAlive();
            _keepAliveCts = new CancellationTokenSource();
            var token = _keepAliveCts.Token;

            model.IsKeepAliveActive       = true;
            model.KeepAliveStatusText     = $"● Active – Pulsing every {model.KeepAliveIntervalSeconds}s → {model.KeepAliveTarget}";
            model.PacketsSent             = 0;
            model.PacketsSuccess          = 0;
            model.PacketsLost             = 0;

            Task.Run(async () =>
            {
                using var ping = new Ping();
                double totalLatency      = 0;
                long   totalSamples      = 0;
                int    pingTargetIndex   = 0;

                while (!token.IsCancellationRequested)
                {
                    string rawTarget = string.IsNullOrWhiteSpace(model.KeepAliveTarget) ? "1.1.1.1" : model.KeepAliveTarget.Trim();
                    // Strip any display suffix like "(Google DNS)" that users might type
                    string target = rawTarget.Split(new[] { ' ', '(', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];

                    double latencyMs = -1;
                    bool   success   = false;
                    string status    = "Timeout";

                    // ── Primary probe ──────────────────────────────────────
                    if (model.KeepAliveMethod.Contains("TCP"))
                    {
                        latencyMs = await ProbeTcpAsync(target, 53, 1500);
                        if (latencyMs < 0) latencyMs = await ProbeTcpAsync(target, 443, 1000);
                        success = latencyMs >= 0;
                        status  = success ? $"TCP OK  {latencyMs:F0} ms" : "TCP Timeout";
                    }
                    else if (model.KeepAliveMethod.Contains("HTTP"))
                    {
                        string probeUrl = !string.IsNullOrWhiteSpace(CaptivePortalProbeUrl)
                            ? CaptivePortalProbeUrl
                            : "http://www.msftconnecttest.com/connecttest.txt";

                        var (pLatency, isCaptive, redirectUrl) = await ProbeHttpAsync(probeUrl, 2000);
                        if (pLatency < 0 && !isCaptive)
                        {
                            // Secondary fallback probe if primary failed
                            (pLatency, isCaptive, redirectUrl) = await ProbeHttpAsync("http://connectivitycheck.gstatic.com/generate_204", 1500);
                        }

                        if (isCaptive)
                        {
                            latencyMs = pLatency;
                            success = false;
                            status = "⚠ Captive Portal Redirect";
                            UI(() =>
                            {
                                model.IsCaptivePortalDetected = true;
                                model.CaptivePortalRedirectUrl = redirectUrl ?? string.Empty;
                                model.CaptivePortalNotice = $"⚠️ Captive Portal Login Required: {redirectUrl}";
                            });
                        }
                        else
                        {
                            latencyMs = pLatency;
                            success = latencyMs >= 0;
                            status = success ? $"HTTP HEAD OK  {latencyMs:F0} ms" : "HTTP Timeout";
                            if (success && model.IsCaptivePortalDetected)
                            {
                                UI(() =>
                                {
                                    model.IsCaptivePortalDetected = false;
                                    model.CaptivePortalNotice = string.Empty;
                                });
                            }
                        }
                    }
                    else // ICMP Ping (default)
                    {
                        // Try rotating targets so a single blocked ICMP doesn't give false drops
                        for (int attempt = 0; attempt < _pingTargets.Length && !success; attempt++)
                        {
                            string t = _pingTargets[(pingTargetIndex + attempt) % _pingTargets.Length];
                            try
                            {
                                var reply = await ping.SendPingAsync(t, 1200);
                                if (reply.Status == IPStatus.Success)
                                {
                                    latencyMs = reply.RoundtripTime;
                                    success   = true;
                                    status    = $"ICMP OK  {latencyMs:F0} ms  ({t})";
                                    break;
                                }
                            }
                            catch { /* try next */ }
                        }

                        // If ICMP fully failed, try TCP fallback to avoid false-positive drops
                        if (!success)
                        {
                            latencyMs = await ProbeTcpAsync("1.1.1.1", 443, 1000);
                            success   = latencyMs >= 0;
                            status    = success ? $"TCP OK  {latencyMs:F0} ms  (ICMP blocked?)" : "All probes failed";
                        }

                        pingTargetIndex = (pingTargetIndex + 1) % _pingTargets.Length;
                    }

                    // ── Quality classification ─────────────────────────────
                    string quality = latencyMs < 0    ? "✗ No Signal"
                                   : latencyMs < 30   ? "Excellent"
                                   : latencyMs < 80   ? "Good"
                                   : latencyMs < 150  ? "Fair"
                                   : latencyMs < 300  ? "Poor"
                                   :                    "Very Poor";

                    // ── Flash pulse animation ──────────────────────────────
                    UI(() => model.IsPulseGlowing = true);

                    double  cap    = latencyMs;
                    bool    capOk  = success;
                    string  capSt  = $"{status}  [{quality}]";

                    UI(() =>
                    {
                        model.PacketsSent++;
                        if (capOk)
                        {
                            model.PacketsSuccess++;
                            model.CurrentLatencyMs = cap;
                            totalLatency += cap;
                            totalSamples++;
                            model.AvgLatencyMs = totalLatency / totalSamples;
                        }
                        else
                        {
                            model.PacketsLost++;
                        }

                        model.KeepAliveStatusText = $"● Active – Last: {capSt} | Loss: {model.PacketLossPercent:F1}%";

                        // Insert new heartbeat entry at front (newest first)
                        model.HeartbeatHistory.Insert(0, new HeartbeatEntry
                        {
                            Time       = DateTime.Now.ToString("HH:mm:ss"),
                            LatencyMs  = cap >= 0 ? cap : 0,
                            IsSuccess  = capOk,
                            StatusText = capSt
                        });

                        int maxHeartbeats = model.KeepAliveLogLimit > 0 ? model.KeepAliveLogLimit : 100;
                        while (model.HeartbeatHistory.Count > maxHeartbeats)
                            model.HeartbeatHistory.RemoveAt(model.HeartbeatHistory.Count - 1);
                    });

                    await Task.Delay(140, token);
                    UI(() => model.IsPulseGlowing = false);

                    int waitMs = Math.Max(1000, model.KeepAliveIntervalSeconds * 1000);
                    await Task.Delay(waitMs, token);
                }
            }, token);
        }

        public void StopKeepAlive()
        {
            _keepAliveCts?.Cancel();
            _keepAliveCts = null;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  ACTIVE RECONNECT
        // ═══════════════════════════════════════════════════════════════════
        public void StartActiveReconnect(ConnectionStabilizerModel model)
        {
            StopActiveReconnect();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;

            var (adapterName, isEthernet) = GetActiveLinkAdapterInfo(model.TargetSsid ?? "");
            string targetDisplay = !string.IsNullOrWhiteSpace(model.TargetSsid) 
                ? model.TargetSsid 
                : (isEthernet ? "Ethernet (Cable Connection)" : "Wi-Fi");

            model.IsReconnectActive  = true;
            model.ReconnectStatusText = $"Watching '{targetDisplay}' (threshold: {model.FailureThreshold} drops)";
            Log(model, $"Active Reconnect watchdog started for '{(isEthernet ? "Ethernet" : "SSID")}: {targetDisplay}' [Adapter: {adapterName}].");

            Task.Run(async () =>
            {
                if (isEthernet)
                {
                    await EnsureEthernetFullSpeedAsync(adapterName, model, forceRecheck: false, token);
                }

                using var ping   = new Ping();
                int  fails       = 0;
                int  checkCycle  = 0;
                DateTime lastReconnectAttempt = DateTime.MinValue;

                while (!token.IsCancellationRequested)
                {
                    // ── Connectivity probe (dual-target for robustness) ────
                    bool alive = await IsInternetAliveAsync(ping);

                    if (alive)
                    {
                        if (fails > 0) Log(model, $"Link recovered after {fails} missed check(s).");
                        fails = 0;

                        // Periodic quality update every 10 cycles
                        if (++checkCycle % 10 == 0)
                        {
                            double lat = await MeasureBestPingAsync(ping);
                            string q   = lat < 0 ? "?" : lat < 60 ? "Excellent" : lat < 150 ? "Good" : lat < 300 ? "Fair" : "Poor";
                            UI(() => model.ReconnectStatusText = $"Link Healthy – {targetDisplay}  [{q} {(lat >= 0 ? lat.ToString("F0") + " ms" : "?")}]");
                        }
                        else
                        {
                            UI(() => model.ReconnectStatusText = $"Link Healthy – {targetDisplay}");
                        }
                    }
                    else
                    {
                        // Check if we are still within the grace buffer from a previous reconnect attempt
                        int bufferSec = Math.Max(5, model.ReconnectBufferSeconds);
                        double elapsedSinceLast = (DateTime.UtcNow - lastReconnectAttempt).TotalSeconds;

                        if (elapsedSinceLast < bufferSec)
                        {
                            int secRemaining = (int)Math.Ceiling(bufferSec - elapsedSinceLast);
                            UI(() => model.ReconnectStatusText = $"Waiting for VPN / DHCP to negotiate ({secRemaining}s grace buffer)...");
                            await Task.Delay(1500, token);
                            continue;
                        }

                        fails++;
                        UI(() => model.ReconnectStatusText = $"⚠ Stalled link – {fails}/{model.FailureThreshold} drops detected");

                        if (fails >= model.FailureThreshold)
                        {
                            lastReconnectAttempt = DateTime.UtcNow;
                            // Immediately reset failure count to prevent rapid re-triggering loops
                            fails = 0;

                            UI(() =>
                            {
                                model.TotalDropsDetected++;
                                model.ReconnectStatusText = "🔄 DROP DETECTED! Initiating reconnect...";
                            });

                            string target = string.IsNullOrWhiteSpace(model.TargetSsid) ? "" : model.TargetSsid.Trim();
                            bool isEthLink = isEthernet || target.Contains("Ethernet", StringComparison.OrdinalIgnoreCase);
                            Log(model, $"[DROP #{model.TotalDropsDetected}] {model.FailureThreshold} consecutive probe failures on {(isEthLink ? "Ethernet" : "Wi-Fi")}. Strategy: {model.ReconnectStrategy}");

                            // ── Execute Recovery strategies ────────────────
                            await ExecuteRecoveryAsync(model, $"Watchdog drop #{model.TotalDropsDetected}", token);
                            lastReconnectAttempt = DateTime.UtcNow;
                            fails = 0;
                        }
                    }

                    await Task.Delay(2000, token);
                }
            }, token);
        }

        /// <summary>
        /// Executes the configured recovery strategy (Adapter Reset, DHCP Renew, Radio Cycle, or Fast Re-auth),
        /// holds the VPN/DHCP grace buffer, verifies restoration, and logs results.
        /// </summary>
        public async Task<bool> ExecuteRecoveryAsync(ConnectionStabilizerModel model, string reason, CancellationToken token = default)
        {
            var (adapterName, isEthernet) = GetActiveLinkAdapterInfo(model.TargetSsid ?? "");
            string target = string.IsNullOrWhiteSpace(model.TargetSsid) ? "" : model.TargetSsid.Trim();
            bool isEthLink = isEthernet || target.Contains("Ethernet", StringComparison.OrdinalIgnoreCase);

            Log(model, $"[RECOVERY] Executing {model.ReconnectStrategy} on {(isEthLink ? "Ethernet" : "Wi-Fi")} (Trigger: {reason})...");

            // ── Execute Recovery strategies ────────────────
            if (isEthLink)
            {
                // Disarm any driver power throttles / 10 Mbps locks before executing recovery
                bool forceRecheck = reason.Contains("Manual", StringComparison.OrdinalIgnoreCase);
                await EnsureEthernetFullSpeedAsync(adapterName, model, forceRecheck, token);

                // ==================== ETHERNET RECONNECTION ====================
                if (model.ReconnectStrategy.Contains("Adapter Reset"))
                {
                    if (!string.IsNullOrEmpty(adapterName) && TryReserveAdapterReset(adapterName, model))
                    {
                        Log(model, $"Resetting Ethernet adapter '{adapterName}' (disable → enable)...");
                        RunCmd("netsh", "interface", "set", "interface", adapterName, "disabled");
                        await Task.Delay(1500, token);
                        RunCmd("netsh", "interface", "set", "interface", adapterName, "enabled");
                        await Task.Delay(4000, token); // Allow 4s for Gigabit/Fast-Ethernet auto-negotiation to complete
                    }
                    RunCmd("ipconfig", "/renew");
                }
                else if (model.ReconnectStrategy.Contains("DHCP"))
                {
                    Log(model, "Renewing Ethernet DHCP lease & flushing DNS...");
                    RunCmd("ipconfig", "/flushdns");
                    RunCmd("ipconfig", "/release");
                    await Task.Delay(800, token);
                    RunCmd("ipconfig", "/renew");
                }
                else // Fast Re-auth / Re-negotiate (default)
                {
                    Log(model, "Re-negotiating Ethernet link & refreshing ARP cache...");
                    RunCmd("netsh", "interface", "ip", "delete", "arpcache");
                    RunCmd("ipconfig", "/renew");
                }
            }
            else
            {
                // ==================== WI-FI RECONNECTION ====================
                if (model.ReconnectStrategy.Contains("Adapter Reset"))
                {
                    if (!string.IsNullOrEmpty(adapterName) && TryReserveAdapterReset(adapterName, model))
                    {
                        Log(model, $"Resetting Wi-Fi adapter '{adapterName}' (disable → enable)...");
                        RunCmd("netsh", "interface", "set", "interface", adapterName, "disabled");
                        await Task.Delay(1200, token);
                        RunCmd("netsh", "interface", "set", "interface", adapterName, "enabled");
                        await Task.Delay(2000, token);
                    }
                    ConnectToWifi(target, adapterName);
                }
                else if (model.ReconnectStrategy.Contains("DHCP"))
                {
                    Log(model, "Reconnecting Wi-Fi and renewing DHCP lease...");
                    ConnectToWifi(target, adapterName);
                    await Task.Delay(1500, token);
                    RunCmd("ipconfig", "/release");
                    await Task.Delay(500, token);
                    RunCmd("ipconfig", "/renew");
                }
                else if (model.ReconnectStrategy.Contains("Radio"))
                {
                    Log(model, "Disconnecting then reconnecting Wi-Fi radio...");
                    RunCmd("netsh", "wlan", "disconnect");
                    await Task.Delay(1000, token);
                    ConnectToWifi(target, adapterName);
                }
                else // Fast Wi-Fi Re-auth (default)
                {
                    Log(model, $"Sending fast re-auth to AP '{target}'...");
                    ConnectToWifi(target, adapterName);
                }
            }

            // ── Buffer / Grace Period (gives Cisco AnyConnect, VPN, DHCP handshake time to establish) ──
            int bufferSec = Math.Max(5, model.ReconnectBufferSeconds);
            Log(model, $"[BUFFER] Reconnect action executed. Holding {bufferSec}s stabilization buffer for VPN & DHCP...");
            UI(() => model.ReconnectStatusText = $"Stabilizing VPN / DHCP link ({bufferSec}s buffer)...");

            DateTime bufferEnd = DateTime.UtcNow.AddSeconds(bufferSec);
            bool restored = false;
            using var ping = new Ping();

            while (DateTime.UtcNow < bufferEnd && !token.IsCancellationRequested)
            {
                int secLeft = (int)Math.Ceiling((bufferEnd - DateTime.UtcNow).TotalSeconds);
                if (secLeft > 0)
                {
                    UI(() => model.ReconnectStatusText = $"Waiting for VPN / DHCP ({secLeft}s buffer remaining)...");
                }

                restored = await IsInternetAliveAsync(ping);
                if (restored)
                {
                    await Task.Delay(2000, token);
                    break;
                }

                await Task.Delay(1500, token);
            }

            if (restored)
            {
                ClearAdapterResetHistory(adapterName);
                UI(() =>
                {
                    model.TotalReconnected++;
                    model.LastReconnectTime = DateTime.Now.ToString("HH:mm:ss");
                    model.ReconnectStatusText = $"✓ Restored at {model.LastReconnectTime}";
                });
                string targetDisplay = !string.IsNullOrWhiteSpace(model.TargetSsid) ? model.TargetSsid.Trim() : (isEthLink ? "Ethernet" : "Active AP");
                Log(model, $"[SUCCESS] Connection successfully restored & stabilized for '{targetDisplay}' at {DateTime.Now:HH:mm:ss} (total: {model.TotalReconnected}).");
            }
            else
            {
                Log(model, $"[BUFFER COMPLETE] Link is still handshaking. Enforcing 8s cooldown before resuming watchdog...");
                UI(() => model.ReconnectStatusText = "VPN / Gateway still establishing (cooling down)...");
                await Task.Delay(8000, token);
            }

            UpdateActiveLinkSpeed(model);
            return restored;
        }

        /// <summary>
        /// Manually triggers an immediate connection reconnect/reset on demand from the UI button.
        /// </summary>
        public async Task<bool> TriggerManualReconnectAsync(ConnectionStabilizerModel model)
        {
            if (model.IsManualReconnecting) return false;
            model.IsManualReconnecting = true;
            try
            {
                Log(model, $"[MANUAL RESET] Manual connection reset triggered by user ({model.ReconnectStrategy}).");
                EthernetSpeedOptimizer.InvalidateAuditCache();
                UI(() => model.ReconnectStatusText = "⚡ Executing manual reconnect & adapter reset...");
                bool ok = await ExecuteRecoveryAsync(model, "Manual Reset Button", CancellationToken.None);
                return ok;
            }
            catch (Exception ex)
            {
                Log(model, $"[MANUAL RESET ERROR] {ex.Message}");
                return false;
            }
            finally
            {
                model.IsManualReconnecting = false;
            }
        }

        /// <summary>
        /// Discovers the active physical network adapter (Ethernet or Wi-Fi) carrying live internet traffic
        /// and calculates its real-time negotiated link speed (1.0 Gbps, 100 Mbps, etc.).
        /// Prevents misidentifying 10 Mbps virtual VPN/TAP/Bluetooth adapters as physical Ethernet.
        /// </summary>
        public void UpdateActiveLinkSpeed(ConnectionStabilizerModel model)
        {
            try
            {
                var ifaces = NetworkInterface.GetAllNetworkInterfaces();
                var physical = ifaces.Where(i => i.OperationalStatus == OperationalStatus.Up && !IsVirtualOrDummyAdapter(i)).ToList();

                // 1. Prioritize physical adapter holding the active IPv4 Default Gateway (carrying live internet traffic)
                var gatewayAdapter = physical.FirstOrDefault(i =>
                {
                    try
                    {
                        var props = i.GetIPProperties();
                        return props != null && props.GatewayAddresses != null &&
                               props.GatewayAddresses.Any(g => g.Address != null &&
                                                              g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                                              !g.Address.Equals(System.Net.IPAddress.Any) &&
                                                              !g.Address.Equals(System.Net.IPAddress.None));
                    }
                    catch { return false; }
                });

                var activeEth = physical.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet);
                var activeWifi = physical.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

                // Priority: Active Gateway adapter > Physical Ethernet > Physical Wi-Fi > Fallback non-virtual
                var primary = gatewayAdapter ?? activeEth ?? activeWifi ?? ifaces.FirstOrDefault(i => i.OperationalStatus == OperationalStatus.Up && !IsVirtualOrDummyAdapter(i));
                if (primary != null)
                {
                    long speedBps = primary.Speed;
                    bool isEth = primary.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
                    string speedFormatted;

                    if (speedBps >= 1_000_000_000)
                    {
                        speedFormatted = $"{speedBps / 1_000_000_000.0:0.#} Gbps";
                    }
                    else if (speedBps >= 100_000_000)
                    {
                        speedFormatted = $"{speedBps / 1_000_000.0:0} Mbps";
                    }
                    else if (speedBps > 10_000_000)
                    {
                        speedFormatted = $"{speedBps / 1_000_000.0:0} Mbps";
                    }
                    else if (speedBps == 10_000_000)
                    {
                        bool isGigabitCapable = primary.Description.Contains("GbE", StringComparison.OrdinalIgnoreCase) ||
                                                primary.Description.Contains("Gigabit", StringComparison.OrdinalIgnoreCase) ||
                                                primary.Description.Contains("1000", StringComparison.OrdinalIgnoreCase) ||
                                                primary.Description.Contains("PCIe", StringComparison.OrdinalIgnoreCase);
                        speedFormatted = isGigabitCapable ? "10 Mbps (Check cable/port)" : "10 Mbps";
                    }
                    else if (speedBps > 0)
                    {
                        speedFormatted = $"{speedBps / 1_000.0:0} Kbps";
                    }
                    else
                    {
                        speedFormatted = "No Link";
                    }

                    string ifType = isEth ? "Ethernet" : "Wi-Fi";
                    string netName = isEth ? primary.Name : (string.IsNullOrWhiteSpace(model.TargetSsid) ? primary.Name : model.TargetSsid);
                    string detail = speedBps <= 0 
                        ? $"(No active link on {primary.Name})" 
                        : (isEth ? $"(Ethernet: {primary.Name})" : $"(Wi-Fi: {netName})");

                    UI(() =>
                    {
                        model.ActiveLinkSpeedText = speedFormatted;
                        model.ActiveInterfaceType = ifType;
                        model.ActiveNetworkName = netName;
                        model.ActiveSpeedSummaryDetail = detail;
                    });
                }
                else
                {
                    UI(() =>
                    {
                        model.ActiveLinkSpeedText = "No Link";
                        model.ActiveInterfaceType = "None";
                        model.ActiveSpeedSummaryDetail = "(No active link)";
                    });
                }
            }
            catch { }
        }

        private static bool IsVirtualOrDummyAdapter(NetworkInterface i)
        {
            if (i.Name.Contains('*')) return true;
            string desc = i.Description ?? "";
            string name = i.Name ?? "";
            return desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Pseudo", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("TAP", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("AnyConnect", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Tunnel", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("PANGP", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("WFP", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                || name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase);
        }

        public void StopActiveReconnect()
        {
            _reconnectCts?.Cancel();
            _reconnectCts = null;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Probes two DNS servers; returns true if at least one responds.</summary>
        private static async Task<bool> IsInternetAliveAsync(Ping ping)
        {
            // Try ICMP first to two independent anycast DNS resolvers
            foreach (string t in new[] { "1.1.1.1", "8.8.8.8" })
            {
                try
                {
                    var r = await ping.SendPingAsync(t, 1000);
                    if (r.Status == IPStatus.Success) return true;
                }
                catch { }
            }

            // ICMP fully failed — try TCP to Cloudflare port 443 as final check
            double tcp = await ProbeTcpAsync("1.1.1.1", 443, 1200);
            return tcp >= 0;
        }

        private static async Task<double> MeasureBestPingAsync(Ping ping)
        {
            var results = new System.Collections.Generic.List<double>();
            foreach (string t in _pingTargets)
            {
                try
                {
                    var r = await ping.SendPingAsync(t, 800);
                    if (r.Status == IPStatus.Success) results.Add(r.RoundtripTime);
                }
                catch { }
            }
            return results.Count > 0 ? results.Min() : -1;
        }

        private static (string name, bool isEthernet) GetActiveLinkAdapterInfo(string target)
        {
            try
            {
                var ifaces = NetworkInterface.GetAllNetworkInterfaces();
                var physical = ifaces.Where(i => !IsVirtualOrDummyAdapter(i)).ToList();

                bool targetEthernet = target.Contains("Ethernet", StringComparison.OrdinalIgnoreCase);

                if (targetEthernet)
                {
                    var eth = physical.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet 
                        && i.OperationalStatus == OperationalStatus.Up);
                    if (eth != null) return (eth.Name, true);

                    var ethFallback = physical.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet);
                    if (ethFallback != null) return (ethFallback.Name, true);
                }

                // Check active adapter with Default Gateway first (carrying live internet traffic)
                var gatewayAdapter = physical.FirstOrDefault(i =>
                {
                    try
                    {
                        if (i.OperationalStatus != OperationalStatus.Up) return false;
                        var props = i.GetIPProperties();
                        return props != null && props.GatewayAddresses != null &&
                               props.GatewayAddresses.Any(g => g.Address != null &&
                                                              g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                                              !g.Address.Equals(System.Net.IPAddress.Any) &&
                                                              !g.Address.Equals(System.Net.IPAddress.None));
                    }
                    catch { return false; }
                });

                if (gatewayAdapter != null)
                {
                    bool isEth = gatewayAdapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
                    return (gatewayAdapter.Name, isEth);
                }

                // Check active Wi-Fi connection
                var wifi = physical.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 
                    && i.OperationalStatus == OperationalStatus.Up);
                if (wifi != null && !targetEthernet)
                {
                    return (wifi.Name, false);
                }

                // Check active Ethernet connection
                var activeEth = physical.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet 
                    && i.OperationalStatus == OperationalStatus.Up);
                if (activeEth != null)
                {
                    return (activeEth.Name, true);
                }

                // Fallbacks
                var fallbackWifi = physical.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                if (fallbackWifi != null) return (fallbackWifi.Name, false);

                var fallbackEth = physical.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet);
                if (fallbackEth != null) return (fallbackEth.Name, true);
            }
            catch { }
            return (string.Empty, false);
        }

        private static async Task<double> ProbeTcpAsync(string host, int port, int timeoutMs)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var client = new TcpClient();
                using var cts    = new CancellationTokenSource(timeoutMs);
                await client.ConnectAsync(host, port, cts.Token);
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds;
            }
            catch { return -1; }
        }

        private static async Task<(double latencyMs, bool isCaptive, string? redirectUrl)> ProbeHttpAsync(string url, int timeoutMs)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var cts  = new CancellationTokenSource(timeoutMs);
                using var req  = new HttpRequestMessage(HttpMethod.Head, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                using var resp = await _probeClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                sw.Stop();

                int code = (int)resp.StatusCode;
                if (code == 301 || code == 302 || code == 307 || code == 308)
                {
                    string? loc = resp.Headers.Location?.ToString();
                    return (sw.Elapsed.TotalMilliseconds, true, loc);
                }

                if (resp.IsSuccessStatusCode)
                {
                    return (sw.Elapsed.TotalMilliseconds, false, null);
                }

                return (-1, false, null);
            }
            catch { return (-1, false, null); }
        }

        private static void Log(ConnectionStabilizerModel model, string message)
        {
            UI(() =>
            {
                model.ReconnectLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
                int maxLogs = model.ReconnectLogLimit > 0 ? model.ReconnectLogLimit : 100;
                while (model.ReconnectLogs.Count > maxLogs)
                    model.ReconnectLogs.RemoveAt(model.ReconnectLogs.Count - 1);
            });
        }

        private static void RunCmd(string exe, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p == null) return;

                // Drain both pipes so a chatty command cannot fill the buffer and hang.
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(true); } catch { }
                }
                Task.WaitAll(new[] { stdout, stderr }, 1000);
            }
            catch { }
        }

        /// <summary>Connects by SSID (not saved profile name) so renamed profiles still work.</summary>
        private static void ConnectToWifi(string ssid, string adapterName)
        {
            if (string.IsNullOrWhiteSpace(ssid)) return;
            var args = new List<string> { "wlan", "connect", $"ssid={ssid}" };
            if (!string.IsNullOrEmpty(adapterName)) args.Add($"interface={adapterName}");
            RunCmd("netsh", args.ToArray());
        }

        /// <summary>
        /// Caps adapter disable/enable cycles to 3 per 10 minutes so a persistently failing
        /// link cannot be thrashed into an unusable state.
        /// </summary>
        private static bool TryReserveAdapterReset(string adapterName, ConnectionStabilizerModel model)
        {
            if (string.IsNullOrEmpty(adapterName)) return false;
            var now = DateTime.UtcNow;
            lock (_adapterResetLock)
            {
                _adapterResetHistory.TryGetValue(adapterName, out var hist);
                bool withinWindow = (now - hist.LastAttempt) < TimeSpan.FromMinutes(10);
                if (withinWindow && hist.Attempts >= 3)
                {
                    Log(model, $"[BACKOFF] Adapter reset throttled for '{adapterName}' (3 attempts in 10 min). Using fast re-auth instead.");
                    return false;
                }
                _adapterResetHistory[adapterName] = (withinWindow ? hist.Attempts + 1 : 1, now);
                return true;
            }
        }

        private static void ClearAdapterResetHistory(string adapterName)
        {
            if (string.IsNullOrEmpty(adapterName)) return;
            lock (_adapterResetLock)
            {
                _adapterResetHistory.Remove(adapterName);
            }
        }

        /// <summary>
        /// Audits and optimizes physical Ethernet adapters to permanently prevent 10 Mbps link throttling.
        /// Disarms Realtek/Intel power-saving mechanisms (WOL &amp; Shutdown Link Speed downshift, Green Ethernet,
        /// Energy-Efficient Ethernet, Advanced EEE, Power Saving Mode, Gigabit Lite, and Auto Disable Gigabit)
        /// and ensures Speed &amp; Duplex is set to Auto Negotiation.
        /// 
        /// Respects the user's PreserveGigabitLinkSpeed opt-in preference and leverages session caching.
        /// </summary>
        public static async Task EnsureEthernetFullSpeedAsync(
            string? adapterName = null,
            ConnectionStabilizerModel? model = null,
            bool forceRecheck = false,
            CancellationToken token = default)
        {
            if (model != null && !model.PreserveGigabitLinkSpeed)
            {
                return;
            }

            try
            {
                await EthernetSpeedOptimizer.AuditAndOptimizeAsync(
                    adapterName,
                    msg => { if (model != null) Log(model, msg); },
                    forceRecheck,
                    token).ConfigureAwait(false);
            }
            catch { }
        }

        private static void UI(Action a)
        {
            if (Application.Current != null && Application.Current.Dispatcher != null)
            {
                try
                {
                    Application.Current.Dispatcher.BeginInvoke(a);
                }
                catch { }
            }
            else
            {
                try { a(); } catch { }
            }
        }
    }
}
