using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class NetworkSettingsService
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WifiAnalyzerHotspot");
        private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

        private readonly HttpClient _dohClient;

        public NetworkSettingsService()
        {
            _dohClient = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(3),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            })
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            _dohClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WifiAnalyzerHotspot/1.1.2");
        }

        // ==================== PERSISTENCE ====================
        public AppSettingsModel LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettingsModel>(json);
                    if (loaded != null)
                    {
                        ApplyProxy(loaded);
                        return loaded;
                    }
                }
            }
            catch { }

            var defaults = new AppSettingsModel();
            ApplyProxy(defaults);
            return defaults;
        }

        public void SaveSettings(AppSettingsModel settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(SettingsFilePath, json);

                ApplyProxy(settings);
                ConfigureWindowsStartup(settings.StartWithWindows, settings.StartMinimized);
            }
            catch { }
        }

        // ==================== WINDOWS STARTUP REGISTRY ====================
        public static void ConfigureWindowsStartup(bool enable, bool startMinimized)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    if (enable)
                    {
                        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WifiAnalyzerHotspot.exe");
                        string args = startMinimized ? " --minimized" : "";
                        key.SetValue("WifiAnalyzerHotspot", $"\"{exePath}\"{args}");
                    }
                    else
                    {
                        key.DeleteValue("WifiAnalyzerHotspot", false);
                    }
                }
            }
            catch { }
        }

        public static bool IsWindowsStartupEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("WifiAnalyzerHotspot") != null;
            }
            catch { return false; }
        }

        // ==================== PROXY APPLICATION & TESTING ====================
        public void ApplyProxy(AppSettingsModel settings)
        {
            try
            {
                if (settings.IsProxyEnabled)
                {
                    if (settings.ProxyType.Contains("System", StringComparison.OrdinalIgnoreCase))
                    {
                        HttpClient.DefaultProxy = WebRequest.GetSystemWebProxy();
                    }
                    else
                    {
                        var proxyUri = new Uri($"http://{settings.ProxyHost.Trim()}:{settings.ProxyPort}");
                        var proxy = new WebProxy(proxyUri, settings.ProxyBypassLocal);
                        if (!string.IsNullOrWhiteSpace(settings.ProxyUsername))
                        {
                            proxy.Credentials = new NetworkCredential(settings.ProxyUsername.Trim(), settings.ProxyPassword ?? "");
                        }
                        HttpClient.DefaultProxy = proxy;
                    }
                }
                else
                {
#pragma warning disable CS8625
                    HttpClient.DefaultProxy = (IWebProxy)null!;
#pragma warning restore CS8625
                }
            }
            catch { }
        }

        public async Task<(bool success, double latencyMs, string message)> TestProxyAsync(AppSettingsModel settings)
        {
            try
            {
                var handler = new HttpClientHandler { UseProxy = true };
                if (settings.ProxyType.Contains("System", StringComparison.OrdinalIgnoreCase))
                {
                    handler.Proxy = WebRequest.GetSystemWebProxy();
                }
                else
                {
                    var proxyUri = new Uri($"http://{settings.ProxyHost.Trim()}:{settings.ProxyPort}");
                    var proxy = new WebProxy(proxyUri, settings.ProxyBypassLocal);
                    if (!string.IsNullOrWhiteSpace(settings.ProxyUsername))
                    {
                        proxy.Credentials = new NetworkCredential(settings.ProxyUsername.Trim(), settings.ProxyPassword ?? "");
                    }
                    handler.Proxy = proxy;
                }

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                var sw = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(5000);
                using var req = new HttpRequestMessage(HttpMethod.Head, "http://www.msftconnecttest.com/connecttest.txt");
                using var resp = await client.SendAsync(req, cts.Token);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                {
                    return (true, sw.Elapsed.TotalMilliseconds, $"✓ Proxy Connected! Latency: {sw.Elapsed.TotalMilliseconds:F0} ms (HTTP {(int)resp.StatusCode})");
                }
                return (false, sw.Elapsed.TotalMilliseconds, $"⚠ Proxy responded with HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
            catch (Exception ex)
            {
                return (false, 0, $"✗ Proxy Connection Failed: {ex.Message}");
            }
        }

        // ==================== SECURE DNS (DoH) TESTING & ADAPTER SETUP ====================
        public async Task<(bool success, double latencyMs, string resolvedIp, string message)> TestDohAsync(string dohUrl, string hostToResolve = "cloudflare.com")
        {
            try
            {
                var sw = Stopwatch.StartNew();
                string queryUrl = dohUrl.Contains("?")
                    ? $"{dohUrl}&name={hostToResolve}&type=A"
                    : $"{dohUrl}?name={hostToResolve}&type=A";

                using var req = new HttpRequestMessage(HttpMethod.Get, queryUrl);
                req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");

                using var cts = new CancellationTokenSource(4000);
                using var resp = await _dohClient.SendAsync(req, cts.Token);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(cts.Token);
                    string ip = ExtractIpFromDnsJson(body);
                    return (true, sw.Elapsed.TotalMilliseconds, ip, $"✓ DoH Query Successful ({sw.Elapsed.TotalMilliseconds:F0} ms) • Resolved {hostToResolve} → {ip}");
                }
                return (false, sw.Elapsed.TotalMilliseconds, "", $"⚠ DoH Query Failed: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                return (false, 0, "", $"✗ DoH Test Error: {ex.Message}");
            }
        }

        private static string ExtractIpFromDnsJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Answer", out var answer) && answer.GetArrayLength() > 0)
                {
                    foreach (var item in answer.EnumerateArray())
                    {
                        if (item.TryGetProperty("data", out var dataProp))
                        {
                            string data = dataProp.GetString() ?? "";
                            if (IPAddress.TryParse(data, out _))
                            {
                                return data;
                            }
                        }
                    }
                }
            }
            catch { }
            return "Active (Resolved)";
        }

        public async Task<(bool success, string message)> ApplyDnsToAdapterAsync(string? adapterName, string provider)
        {
            if (string.IsNullOrWhiteSpace(adapterName))
                return (false, "No active network adapter found to configure.");

            string primaryIp = "1.1.1.1";
            string secondaryIp = "1.0.0.1";

            if (provider.Contains("Google"))
            {
                primaryIp = "8.8.8.8";
                secondaryIp = "8.8.4.4";
            }
            else if (provider.Contains("Quad9"))
            {
                primaryIp = "9.9.9.9";
                secondaryIp = "149.112.112.112";
            }
            else if (provider.Contains("AdGuard"))
            {
                primaryIp = "94.140.14.14";
                secondaryIp = "94.140.15.15";
            }

            try
            {
                await Task.Run(() =>
                {
                    RunProcess("netsh", $"interface ipv4 set dnsservers name=\"{adapterName}\" static {primaryIp} primary validate=no");
                    RunProcess("netsh", $"interface ipv4 add dnsservers name=\"{adapterName}\" {secondaryIp} index=2 validate=no");
                    RunProcess("ipconfig", "/flushdns");
                });

                return (true, $"✓ Configured {provider} ({primaryIp}, {secondaryIp}) on adapter '{adapterName}'. DNS cache flushed.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to configure adapter DNS: {ex.Message}");
            }
        }

        public async Task<(bool success, string message)> RestoreDhcpDnsAsync(string? adapterName)
        {
            if (string.IsNullOrWhiteSpace(adapterName))
                return (false, "No active network adapter found.");

            try
            {
                await Task.Run(() =>
                {
                    RunProcess("netsh", $"interface ipv4 set dnsservers name=\"{adapterName}\" source=dhcp");
                    RunProcess("ipconfig", "/flushdns");
                });

                return (true, $"✓ Restored automatic DHCP DNS on adapter '{adapterName}'.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to restore DHCP DNS: {ex.Message}");
            }
        }

        // ==================== CAPTIVE PORTAL HTTP HEAD PROBE ====================
        public async Task<(bool isCaptivePortal, int statusCode, string? redirectUrl, double latencyMs, string message)> ProbeCaptivePortalAsync(string probeUrl, int timeoutMs = 2500)
        {
            try
            {
                // Disallow automatic redirect to capture HTTP 302/307 captive portal redirection
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");

                var sw = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(timeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Head, probeUrl);
                using var resp = await client.SendAsync(req, cts.Token);
                sw.Stop();

                int code = (int)resp.StatusCode;
                if (code == 301 || code == 302 || code == 307 || code == 308)
                {
                    string redirect = resp.Headers.Location?.ToString() ?? "Captive Login Page";
                    return (true, code, redirect, sw.Elapsed.TotalMilliseconds,
                        $"⚠ Captive Portal Login Detected (HTTP {code})! Redirect: {redirect}");
                }

                if (resp.IsSuccessStatusCode)
                {
                    return (false, code, null, sw.Elapsed.TotalMilliseconds,
                        $"✓ Internet Connected • HTTP {code} ({sw.Elapsed.TotalMilliseconds:F0} ms). No Captive Portal.");
                }

                return (false, code, null, sw.Elapsed.TotalMilliseconds,
                    $"HTTP HEAD returned status {code} ({sw.Elapsed.TotalMilliseconds:F0} ms)");
            }
            catch (Exception ex)
            {
                return (false, 0, null, 0, $"Probe error: {ex.Message}");
            }
        }

        private static void RunProcess(string filename, string args)
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = filename,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                p.Start();
                p.WaitForExit(3000);
            }
            catch { }
        }
    }
}
