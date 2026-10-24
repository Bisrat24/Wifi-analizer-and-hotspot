using System;
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

                        while (model.HeartbeatHistory.Count > 300)
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
                            if (isEthLink)
                            {
                                // ==================== ETHERNET RECONNECTION ====================
                                if (model.ReconnectStrategy.Contains("Adapter Reset"))
                                {
                                    Log(model, $"Resetting Ethernet adapter '{adapterName}' (disable → enable)...");
                                    if (!string.IsNullOrEmpty(adapterName))
                                    {
                                        RunCmd("netsh", $"interface set interface \"{adapterName}\" disabled");
                                        await Task.Delay(1500, token);
                                        RunCmd("netsh", $"interface set interface \"{adapterName}\" enabled");
                                        await Task.Delay(2500, token);
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
                                    RunCmd("netsh", "interface ip delete arpcache");
                                    RunCmd("ipconfig", "/renew");
                                }
                            }
                            else
                            {
                                // ==================== WI-FI RECONNECTION ====================
                                if (model.ReconnectStrategy.Contains("Adapter Reset"))
                                {
                                    Log(model, "Resetting Wi-Fi adapter (disable → enable)...");
                                    if (!string.IsNullOrEmpty(adapterName))
                                    {
                                        RunCmd("netsh", $"interface set interface \"{adapterName}\" disabled");
                                        await Task.Delay(1200, token);
                                        RunCmd("netsh", $"interface set interface \"{adapterName}\" enabled");
                                        await Task.Delay(2000, token);
                                    }
                                    if (!string.IsNullOrEmpty(target))
                                        RunCmd("netsh", $"wlan connect name=\"{target}\"");
                                }
                                else if (model.ReconnectStrategy.Contains("DHCP"))
                                {
                                    Log(model, "Reconnecting Wi-Fi and renewing DHCP lease...");
                                    if (!string.IsNullOrEmpty(target))
                                        RunCmd("netsh", $"wlan connect name=\"{target}\"");
                                    await Task.Delay(1500, token);
                                    RunCmd("ipconfig", "/release");
                                    await Task.Delay(500, token);
                                    RunCmd("ipconfig", "/renew");
                                }
                                else if (model.ReconnectStrategy.Contains("Radio"))
                                {
                                    Log(model, "Disconnecting then reconnecting Wi-Fi radio...");
                                    RunCmd("netsh", "wlan disconnect");
                                    await Task.Delay(1000, token);
                                    if (!string.IsNullOrEmpty(target))
                                        RunCmd("netsh", $"wlan connect name=\"{target}\"");
                                }
                                else // Fast Wi-Fi Re-auth (default)
                                {
                                    Log(model, $"Sending fast re-auth to AP '{target}'...");
                                    if (!string.IsNullOrEmpty(target))
                                        RunCmd("netsh", $"wlan connect name=\"{target}\"");
                                }
                            }

                            // ── Buffer / Grace Period (gives Cisco AnyConnect, VPN, DHCP handshake time to establish) ──
                            Log(model, $"[BUFFER] Reconnect action executed. Holding {bufferSec}s stabilization buffer for VPN (Cisco AnyConnect) & DHCP...");
                            UI(() => model.ReconnectStatusText = $"Stabilizing VPN / DHCP link ({bufferSec}s buffer)...");

                            DateTime bufferEnd = DateTime.UtcNow.AddSeconds(bufferSec);
                            bool restored = false;

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
                                    // Give AnyConnect or corporate VPN 2 extra seconds to stabilize routes
                                    await Task.Delay(2000, token);
                                    break;
                                }

                                await Task.Delay(1500, token);
                            }

                            if (restored)
                            {
                                UI(() =>
                                {
                                    model.TotalReconnected++;
                                    model.LastReconnectTime = DateTime.Now.ToString("HH:mm:ss");
                                    model.ReconnectStatusText = $"✓ Restored at {model.LastReconnectTime}";
                                });
                                Log(model, $"[SUCCESS] Connection successfully restored & stabilized for '{targetDisplay}' at {DateTime.Now:HH:mm:ss} (total: {model.TotalReconnected}).");
                                fails = 0;
                            }
                            else
                            {
                                Log(model, $"[BUFFER COMPLETE] Link is still handshaking. Enforcing 8s cooldown before resuming watchdog...");
                                UI(() => model.ReconnectStatusText = "VPN / Gateway still establishing (cooling down)...");
                                fails = 0; // Prevent immediate flapped re-trigger!
                                await Task.Delay(8000, token);
                            }
                        }
                    }

                    await Task.Delay(2000, token);
                }
            }, token);
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

                bool targetEthernet = target.Contains("Ethernet", StringComparison.OrdinalIgnoreCase);

                if (targetEthernet)
                {
                    var eth = ifaces.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet 
                        && !i.Name.Contains('*') 
                        && i.OperationalStatus != OperationalStatus.NotPresent);
                    if (eth != null) return (eth.Name, true);
                }

                // Check active Wi-Fi connection
                var wifi = ifaces.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 
                    && !i.Name.Contains('*') 
                    && i.OperationalStatus == OperationalStatus.Up);
                if (wifi != null && !targetEthernet)
                {
                    return (wifi.Name, false);
                }

                // Check active Ethernet connection
                var activeEth = ifaces.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet 
                    && !i.Name.Contains('*') 
                    && i.OperationalStatus == OperationalStatus.Up);
                if (activeEth != null)
                {
                    return (activeEth.Name, true);
                }

                // Fallbacks
                var fallbackEth = ifaces.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet && !i.Name.Contains('*'));
                if (fallbackEth != null) return (fallbackEth.Name, true);

                var fallbackWifi = ifaces.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && !i.Name.Contains('*'));
                if (fallbackWifi != null) return (fallbackWifi.Name, false);
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
                while (model.ReconnectLogs.Count > 300)
                    model.ReconnectLogs.RemoveAt(model.ReconnectLogs.Count - 1);
            });
        }

        private static void RunCmd(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = exe,
                    Arguments              = args,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(true); } catch { }
                    }
                }
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
