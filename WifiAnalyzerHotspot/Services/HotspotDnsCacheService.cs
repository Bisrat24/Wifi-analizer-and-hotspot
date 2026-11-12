using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Ultra-Low-Latency DNS Caching Forwarder for Hotspot Clients.
    /// Intercepts DNS queries on the hotspot subnet (192.168.137.1:53), resolves them via
    /// Local / Corporate DNS servers (discovered from active host adapters for enterprise intranet compatibility)
    /// with Anycast fallback, validates responses, and caches in memory for sub-millisecond repeated lookups.
    /// Safely reports inactive when port 53 is exclusively bound by Windows ICS.
    /// </summary>
    public class HotspotDnsCacheService : IDisposable
    {
        private static readonly IPEndPoint[] DefaultAnycastDnsServers = new[]
        {
            new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53),
            new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53),
            new IPEndPoint(IPAddress.Parse("1.0.0.1"), 53)
        };

        private const int MaxCacheEntries = 2000;
        private readonly ConcurrentDictionary<string, (byte[] Response, DateTime ExpiresAt)> _cache = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _concurrencyLimiter = new(16, 16);
        private readonly List<IPEndPoint> _activeUpstreamServers = new();
        private string _upstreamDescription = "Local / Corporate DNS";
        private Socket? _listenerSocket;
        private CancellationTokenSource? _cts;
        private volatile bool _isRunning;
        private long _totalQueries;
        private long _cacheHits;

        public bool IsRunning => _isRunning;
        public long CacheHits => Interlocked.Read(ref _cacheHits);
        public long TotalQueries => Interlocked.Read(ref _totalQueries);
        public string? LastError { get; private set; }

        public string GetStatsSummary()
        {
            if (!_isRunning)
            {
                if (!string.IsNullOrEmpty(LastError))
                {
                    return $"DNS: Inactive ({LastError})";
                }
                return "DNS: Stopped (Starts with Hotspot)";
            }
            long queries = TotalQueries;
            long hits = CacheHits;
            double hitRate = queries > 0 ? (hits * 100.0 / queries) : 0;
            return $"⚡ DNS Cache: {hits} hits / {queries} queries ({hitRate:F0}%, {_cache.Count} cached) • {_upstreamDescription}";
        }

        public event Action<string>? OnLog;

        /// <summary>
        /// Detects active upstream DNS servers configured on physical/primary host network adapters
        /// (e.g. corporate domain controllers or ISP routers), ensuring corporate intranet names
        /// resolve properly and avoiding blocks on public Anycast IPs.
        /// </summary>
        public static List<IPEndPoint> GetLocalDnsServers()
        {
            var servers = new List<IPEndPoint>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    // Exclude SoftAP virtual adapters
                    string desc = nic.Description ?? "";
                    string name = nic.Name ?? "";
                    if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Direct", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Local Area Connection*")) continue;

                    var props = nic.GetIPProperties();
                    foreach (var dns in props.DnsAddresses)
                    {
                        if (dns.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(dns) &&
                            !dns.ToString().StartsWith("192.168.137."))
                        {
                            if (!servers.Any(s => s.Address.Equals(dns)))
                            {
                                servers.Add(new IPEndPoint(dns, 53));
                            }
                        }
                    }
                }
            }
            catch { }
            return servers;
        }

        /// <summary>
        /// Attempts to bind to the Hotspot gateway IP (192.168.137.1:53).
        /// Returns true if bound and active; false if binding failed (e.g. held by ICS).
        /// </summary>
        public bool Start(string bindIp = "192.168.137.1")
        {
            if (_isRunning) return true;

            // Clean up any prior CTS to prevent token leaks
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
            catch { }

            try
            {
                if (!IPAddress.TryParse(bindIp, out var listenAddress))
                {
                    _isRunning = false;
                    LastError = $"Invalid IP address '{bindIp}'";
                    return false;
                }

                // Discover local / corporate DNS servers from active network adapters
                var localServers = GetLocalDnsServers();
                _activeUpstreamServers.Clear();
                if (localServers.Count > 0)
                {
                    _activeUpstreamServers.AddRange(localServers);
                    string serverIps = string.Join(", ", localServers.Take(2).Select(s => s.Address.ToString()));
                    _upstreamDescription = $"Local/Corporate DNS ({serverIps})";
                }
                else
                {
                    _activeUpstreamServers.AddRange(DefaultAnycastDnsServers);
                    _upstreamDescription = "Cloudflare (1.1.1.1) / Google (8.8.8.8)";
                }

                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _listenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                // Never bind to IPAddress.Any (prevents open-resolver vulnerability)
                _listenerSocket.Bind(new IPEndPoint(listenAddress, 53));
                _isRunning = true;
                LastError = null;

                OnLog?.Invoke($"[DNS-Cache] Zero-Latency DNS Cache active on {listenAddress}:53 (Upstream: {_upstreamDescription})");

                Task.Run(() => ListenLoopAsync(_listenerSocket, token), token);
                return true;
            }
            catch (Exception ex)
            {
                _isRunning = false;
                LastError = "Port 53 in use by Windows ICS";
                try
                {
                    _listenerSocket?.Close();
                    _listenerSocket?.Dispose();
                    _listenerSocket = null;
                }
                catch { }

                try
                {
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _cts = null;
                }
                catch { }

                OnLog?.Invoke($"[DNS-Cache] Notice: Port 53 bound by Windows ICS ({ex.Message}). Hotspot using standard system DNS gateway.");
                return false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
            catch { }

            try
            {
                _listenerSocket?.Close();
                _listenerSocket?.Dispose();
                _listenerSocket = null;
            }
            catch { }
            OnLog?.Invoke("[DNS-Cache] DNS Cache service stopped.");
        }

        private async Task ListenLoopAsync(Socket socket, CancellationToken token)
        {
            byte[] buffer = new byte[2048];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var result = await socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, remoteEp).ConfigureAwait(false);
                    if (result.ReceivedBytes <= 12) continue; // Minimum DNS header is 12 bytes

                    Interlocked.Increment(ref _totalQueries);
                    byte[] queryBytes = new byte[result.ReceivedBytes];
                    Array.Copy(buffer, queryBytes, result.ReceivedBytes);
                    var clientEp = result.RemoteEndPoint;

                    // Limit concurrent background query forwarders
                    _ = Task.Run(async () =>
                    {
                        await _concurrencyLimiter.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            await HandleQueryAsync(socket, queryBytes, clientEp, token).ConfigureAwait(false);
                        }
                        finally
                        {
                            _concurrencyLimiter.Release();
                        }
                    }, token);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) when (!_isRunning) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DNS listener loop exception: {ex.Message}");
                }
            }
        }

        private async Task HandleQueryAsync(Socket socket, byte[] query, EndPoint clientEp, CancellationToken token)
        {
            try
            {
                ushort queryId = (ushort)((query[0] << 8) | query[1]);
                string cacheKey = ExtractCacheKey(query);

                // 1. Check local in-memory cache
                if (!string.IsNullOrEmpty(cacheKey) && _cache.TryGetValue(cacheKey, out var entry))
                {
                    if (DateTime.UtcNow < entry.ExpiresAt)
                    {
                        Interlocked.Increment(ref _cacheHits);
                        byte[] cachedResponse = (byte[])entry.Response.Clone();
                        // Rewrite Transaction ID to match client's query
                        cachedResponse[0] = (byte)(queryId >> 8);
                        cachedResponse[1] = (byte)(queryId & 0xFF);

                        await socket.SendToAsync(new ArraySegment<byte>(cachedResponse), SocketFlags.None, clientEp).ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        _cache.TryRemove(cacheKey, out _);
                    }
                }

                // 2. Upstream Parallel Query (using Local / Corporate DNS first)
                byte[]? upstreamResponse = await ForwardQueryParallelAsync(query, token).ConfigureAwait(false);
                if (upstreamResponse != null && upstreamResponse.Length >= 12)
                {
                    // Rewrite transaction ID to match incoming client query
                    upstreamResponse[0] = (byte)(queryId >> 8);
                    upstreamResponse[1] = (byte)(queryId & 0xFF);

                    int rcode = upstreamResponse[3] & 0x0F;
                    bool isTruncated = (upstreamResponse[2] & 0x02) != 0;

                    // Cache response
                    if (!string.IsNullOrEmpty(cacheKey) && !isTruncated)
                    {
                        if (_cache.Count >= MaxCacheEntries)
                        {
                            PruneExpiredCache();
                        }

                        if (_cache.Count < MaxCacheEntries)
                        {
                            if (rcode == 0) // NoError: Cache for extracted TTL (min 15s, max 3600s)
                            {
                                uint ttl = ExtractTtl(upstreamResponse);
                                var expiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(ttl, 15, 3600));
                                _cache[cacheKey] = (upstreamResponse, expiresAt);
                            }
                            else if (rcode == 3) // NXDOMAIN: Negative caching for 30 seconds
                            {
                                var expiresAt = DateTime.UtcNow.AddSeconds(30);
                                _cache[cacheKey] = (upstreamResponse, expiresAt);
                            }
                        }
                    }

                    // Always send response back to client (even for NXDOMAIN, SERVFAIL, or Truncated)
                    // so client DNS resolvers never hang waiting for timeout
                    await socket.SendToAsync(new ArraySegment<byte>(upstreamResponse), SocketFlags.None, clientEp).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DNS query handling error: {ex.Message}");
            }
        }

        private void PruneExpiredCache()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _cache)
            {
                if (now >= kvp.Value.ExpiresAt)
                {
                    _cache.TryRemove(kvp.Key, out _);
                }
            }
        }

        private async Task<byte[]?> ForwardQueryParallelAsync(byte[] query, CancellationToken token)
        {
            using var forwardCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            forwardCts.CancelAfter(2500); // 2.5s max timeout

            IReadOnlyList<IPEndPoint> servers = _activeUpstreamServers.Count > 0 ? _activeUpstreamServers : DefaultAnycastDnsServers;
            var tasks = new List<Task<byte[]?>>(servers.Count);
            for (int i = 0; i < servers.Count; i++)
            {
                tasks.Add(QuerySingleUpstreamAsync(query, servers[i], forwardCts.Token));
            }

            byte[]? fallbackResponse = null;
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completed);

                try
                {
                    byte[]? res = await completed.ConfigureAwait(false);
                    if (res != null && res.Length >= 12)
                    {
                        int rcode = res[3] & 0x0F;
                        if (rcode == 0) // Clean NoError answer: return immediately
                        {
                            try { forwardCts.Cancel(); } catch { }
                            return res;
                        }
                        // Save NXDOMAIN or error answer in case no server provides NoError
                        fallbackResponse ??= res;
                    }
                }
                catch { }
            }

            return fallbackResponse;
        }

        private static async Task<byte[]?> QuerySingleUpstreamAsync(byte[] query, IPEndPoint server, CancellationToken token)
        {
            try
            {
                using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                client.SendTimeout = 2000;
                client.ReceiveTimeout = 2000;

                // Connect to server endpoint so socket only receives packets from this exact upstream server
                client.Connect(server);

                await client.SendAsync(new ArraySegment<byte>(query), SocketFlags.None).ConfigureAwait(false);

                byte[] receiveBuf = new byte[2048];
                var res = await client.ReceiveAsync(new ArraySegment<byte>(receiveBuf), SocketFlags.None).ConfigureAwait(false);

                if (res >= 12)
                {
                    // 1. Transaction ID verification
                    if (receiveBuf[0] != query[0] || receiveBuf[1] != query[1])
                    {
                        return null; // ID mismatch: drop forged packet
                    }

                    // 2. DNS Flags verification: Byte 2, bit 7: QR (0 = query, 1 = response)
                    bool isResponse = (receiveBuf[2] & 0x80) != 0;
                    if (!isResponse)
                    {
                        return null;
                    }

                    byte[] result = new byte[res];
                    Array.Copy(receiveBuf, result, res);
                    return result;
                }
            }
            catch { }
            return null;
        }

        private static string ExtractCacheKey(byte[] query)
        {
            if (query.Length <= 12) return string.Empty;
            // Ignore transaction ID (first 2 bytes) and query flags for the cache key
            return Convert.ToBase64String(query, 12, query.Length - 12);
        }

        private static uint ExtractTtl(byte[] response)
        {
            try
            {
                int pos = 12;
                while (pos < response.Length && response[pos] != 0)
                {
                    if ((response[pos] & 0xC0) == 0xC0) { pos += 2; break; }
                    pos += response[pos] + 1;
                }
                if (pos < response.Length && response[pos] == 0) pos++;
                pos += 4; // Skip QTYPE and QCLASS

                if (pos + 10 <= response.Length)
                {
                    if ((response[pos] & 0xC0) == 0xC0) pos += 2;
                    pos += 4; // Skip TYPE & CLASS
                    if (pos + 4 <= response.Length)
                    {
                        uint ttl = (uint)((response[pos] << 24) | (response[pos + 1] << 16) | (response[pos + 2] << 8) | response[pos + 3]);
                        return ttl;
                    }
                }
            }
            catch { }
            return 300; // Default 5 minutes
        }

        public void Dispose()
        {
            Stop();
            _cache.Clear();
            _concurrencyLimiter.Dispose();
        }
    }
}
