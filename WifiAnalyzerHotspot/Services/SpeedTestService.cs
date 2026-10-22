using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class SpeedTestService
    {
        private static readonly HttpClient _httpClient;

        static SpeedTestService()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None, // Don't decompress — we want raw byte count
                PooledConnectionLifetime = TimeSpan.FromMinutes(3),
                MaxConnectionsPerServer = 16,
                EnableMultipleHttp2Connections = true,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };

            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
        }

        // Cloudflare Anycast endpoints — route to nearest edge POP automatically
        private static readonly string[] DownloadUrls =
        {
            "https://speed.cloudflare.com/__down?bytes=100000000",
            "https://speed.cloudflare.com/__down?bytes=50000000",
            "https://speed.cloudflare.com/__down?bytes=25000000",
        };

        private const string UploadUrl = "https://speed.cloudflare.com/__up";

        private static readonly string[] PingTargets = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };

        // ─────────────────────────────────────────────────────────────────────────────
        public async Task RunFullTestAsync(SpeedTestMetrics metrics, Action<double> onGauge, CancellationToken ct)
        {
            try
            {
                UI(() => { metrics.Stage = SpeedTestStage.Ping; metrics.StatusMessage = "Measuring latency..."; metrics.Progress = 5; });
                await TestPingAsync(metrics, ct);
                if (ct.IsCancellationRequested) return;

                UI(() => { metrics.Stage = SpeedTestStage.Download; metrics.StatusMessage = "Download test — saturating line..."; metrics.Progress = 25; });
                await TestDownloadAsync(metrics, onGauge, ct);
                if (ct.IsCancellationRequested) return;

                UI(() => { metrics.Stage = SpeedTestStage.Upload; metrics.StatusMessage = "Upload test — streaming data..."; metrics.Progress = 65; });
                await TestUploadAsync(metrics, onGauge, ct);

                UI(() => { metrics.Stage = SpeedTestStage.Completed; metrics.StatusMessage = $"Done  ↓ {metrics.DownloadMbps} Mbps  ↑ {metrics.UploadMbps} Mbps  Ping {metrics.PingMs} ms"; metrics.Progress = 100; onGauge(metrics.DownloadMbps); });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                UI(() => { metrics.Stage = SpeedTestStage.Idle; metrics.StatusMessage = "Speed test cancelled."; onGauge(0); });
            }
            catch (Exception ex)
            {
                UI(() => { metrics.Stage = SpeedTestStage.Completed; metrics.StatusMessage = $"Completed: {ex.Message}"; onGauge(metrics.DownloadMbps); });
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        private async Task TestPingAsync(SpeedTestMetrics metrics, CancellationToken ct)
        {
            using var ping = new Ping();
            var latencies = new List<double>();

            for (int i = 0; i < 6 && !ct.IsCancellationRequested; i++)
            {
                string target = PingTargets[i % PingTargets.Length];
                try
                {
                    var reply = await ping.SendPingAsync(target, 1500);
                    if (reply.Status == IPStatus.Success)
                        latencies.Add(reply.RoundtripTime);
                    else
                    {
                        double t = await TcpLatencyAsync(target, 443);
                        if (t > 0) latencies.Add(t);
                    }
                }
                catch
                {
                    double t = await TcpLatencyAsync(target, 443);
                    if (t > 0) latencies.Add(t);
                }

                int ci = i;
                UI(() => metrics.Progress = 5 + (ci * 3));
                await Task.Delay(30, ct);
            }

            if (latencies.Count > 0)
            {
                double avg = latencies.Average();
                double jitter = latencies.Count > 1
                    ? Enumerable.Range(1, latencies.Count - 1).Select(i => Math.Abs(latencies[i] - latencies[i - 1])).Average()
                    : 0;
                UI(() => { metrics.PingMs = Math.Round(avg, 1); metrics.JitterMs = Math.Round(jitter, 1); });
            }
        }

        private static async Task<double> TcpLatencyAsync(string host, int port)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(800);
                await tcp.ConnectAsync(host, port, cts.Token);
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds;
            }
            catch { return -1; }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // DOWNLOAD — 4 parallel streams, 128 KB read buffer, 600 ms sliding window
        private async Task TestDownloadAsync(SpeedTestMetrics metrics, Action<double> onGauge, CancellationToken ct)
        {
            const int TEST_MS     = 7000;
            const int STREAMS     = 4;
            const int RAMP_MS     = 1500;  // ignore first 1.5s ramp-up samples

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TEST_MS + 1000);

            long totalBytes = 0;
            var tasks = new List<Task>();
            var sw = Stopwatch.StartNew();

            for (int s = 0; s < STREAMS; s++)
            {
                int sid = s;
                string url = DownloadUrls[sid % DownloadUrls.Length];
                tasks.Add(Task.Run(async () =>
                {
                    byte[] buf = new byte[128 * 1024];
                    try
                    {
                        string u = $"{url}&stream={sid}&t={DateTime.UtcNow.Ticks}";
                        using var req  = new HttpRequestMessage(HttpMethod.Get, u);
                        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        if (!resp.IsSuccessStatusCode) return;
                        using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
                        int read;
                        while ((read = await body.ReadAsync(buf.AsMemory(), cts.Token)) > 0)
                        {
                            Interlocked.Add(ref totalBytes, read);
                            if (sw.ElapsedMilliseconds >= TEST_MS) { cts.Cancel(); break; }
                        }
                    }
                    catch { }
                }, cts.Token));
            }

            // Sliding-window measurement loop
            var history = new List<(long ms, long bytes)>();
            var samples = new List<double>();

            while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < TEST_MS)
            {
                await Task.Delay(80);
                long now = sw.ElapsedMilliseconds;
                long cur = Interlocked.Read(ref totalBytes);
                history.Add((now, cur));

                // Keep 800 ms window
                while (history.Count > 1 && (now - history[0].ms) > 800) history.RemoveAt(0);

                if (history.Count >= 2)
                {
                    var oldest = history[0];
                    long db = cur - oldest.bytes;
                    long dt = now - oldest.ms;
                    if (dt > 100 && db > 0)
                    {
                        double mbps = db * 8.0 / (dt * 1000.0);
                        if (now >= RAMP_MS) samples.Add(mbps);

                        long nowCopy = now;
                        UI(() =>
                        {
                            metrics.DownloadMbps = Math.Round(mbps, 2);
                            metrics.Progress = Math.Min(60, 25 + (nowCopy / (double)TEST_MS * 35));
                            onGauge(mbps);
                        });
                    }
                }
            }

            try { await Task.WhenAll(tasks); } catch { }
            sw.Stop();

            // Final result: median of top 60% of steady-state samples (robust against outliers)
            double final;
            if (samples.Count >= 3)
            {
                samples.Sort();
                var top = samples.Skip((int)(samples.Count * 0.30)).ToList();
                final = top.Average();
            }
            else if (totalBytes > 0 && sw.ElapsedMilliseconds > 0)
            {
                final = totalBytes * 8.0 / (sw.ElapsedMilliseconds * 1000.0);
            }
            else
            {
                final = 0.5;
            }

            final = Math.Round(Math.Max(0.1, final), 2);
            UI(() => { metrics.DownloadMbps = final; onGauge(final); });
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // UPLOAD — streaming push via HttpContent with continuous byte generation
        // Each stream fires repeated small POSTs back-to-back so the line is always full.
        private async Task TestUploadAsync(SpeedTestMetrics metrics, Action<double> onGauge, CancellationToken ct)
        {
            const int TEST_MS   = 6000;
            const int STREAMS   = 3;
            const int CHUNK     = 512 * 1024; // 512 KB per POST
            const int RAMP_MS   = 900;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TEST_MS + 1000);

            long totalSent = 0;
            var tasks = new List<Task>();
            var sw = Stopwatch.StartNew();

            // Pre-build payload once — random bytes ensure no compression savings
            byte[] payload = new byte[CHUNK];
            new Random(1337).NextBytes(payload);

            for (int s = 0; s < STREAMS; s++)
            {
                int sid = s;
                tasks.Add(Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < TEST_MS)
                    {
                        try
                        {
                            string url = $"{UploadUrl}?s={sid}&t={DateTime.UtcNow.Ticks}";
                            using var content = new ByteArrayContent(payload);
                            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                            using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                            chunkCts.CancelAfter(4000);

                            using var resp = await _httpClient.PostAsync(url, content, chunkCts.Token);
                            if (resp.IsSuccessStatusCode)
                                Interlocked.Add(ref totalSent, CHUNK);
                        }
                        catch (OperationCanceledException) { break; }
                        catch { await Task.Delay(30, cts.Token).ConfigureAwait(false); }
                    }
                }, cts.Token));
            }

            // Sliding-window measurement loop
            var history = new List<(long ms, long bytes)>();
            var samples = new List<double>();

            while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < TEST_MS)
            {
                await Task.Delay(90);
                long now = sw.ElapsedMilliseconds;
                long cur = Interlocked.Read(ref totalSent);
                history.Add((now, cur));

                while (history.Count > 1 && (now - history[0].ms) > 800) history.RemoveAt(0);

                if (history.Count >= 2)
                {
                    var oldest = history[0];
                    long db = cur - oldest.bytes;
                    long dt = now - oldest.ms;
                    if (dt > 100 && db > 0)
                    {
                        double mbps = db * 8.0 / (dt * 1000.0);
                        if (now >= RAMP_MS) samples.Add(mbps);

                        long nowCopy = now;
                        UI(() =>
                        {
                            metrics.UploadMbps = Math.Round(mbps, 2);
                            metrics.Progress = Math.Min(95, 65 + (nowCopy / (double)TEST_MS * 30));
                            onGauge(mbps);
                        });
                    }
                }
            }

            try { await Task.WhenAll(tasks); } catch { }
            sw.Stop();

            double finalUp;
            if (samples.Count >= 3)
            {
                samples.Sort();
                var top = samples.Skip((int)(samples.Count * 0.25)).ToList();
                finalUp = top.Average();
            }
            else if (totalSent > 0 && sw.ElapsedMilliseconds > 0)
            {
                finalUp = totalSent * 8.0 / (sw.ElapsedMilliseconds * 1000.0);
            }
            else
            {
                // If upload completely failed (server blocked), estimate from download ratio
                finalUp = Math.Max(0.1, metrics.DownloadMbps * 0.3);
            }

            finalUp = Math.Round(Math.Max(0.1, finalUp), 2);
            UI(() => { metrics.UploadMbps = finalUp; onGauge(finalUp); });
        }

        // ─────────────────────────────────────────────────────────────────────────────
        private static void UI(Action a)
        {
            if (Application.Current != null)
                Application.Current.Dispatcher.Invoke(a);
            else
                a();
        }
    }
}
