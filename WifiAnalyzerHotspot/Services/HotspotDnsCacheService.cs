using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Ultra-Low-Latency DNS Caching Forwarder for Hotspot Clients.
    /// Intercepts DNS queries on the hotspot subnet (192.168.137.1:53).
    /// Resolves via Local / Corporate DNS servers (discovered from active host adapters including loopback stub resolvers)
    /// with graceful Anycast fallback (Cloudflare 1.1.1.1 / Google 8.8.8.8), validates responses, and caches in memory
    /// for sub-millisecond repeated lookups.
    /// Provides UDP + TCP listeners, genuine token-bound timeouts, RFC-compliant SERVFAIL synthesis on timeout,
    /// and dynamic upstream refresh on network changes.
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

        private const int MaxCacheEntries = 50000;
        private readonly ConcurrentDictionary<string, (byte[] Response, DateTime ExpiresAt, bool IsLocal)> _cache = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _concurrencyLimiter = new(16, 16);
        private volatile IPEndPoint[] _localUpstreamServers = Array.Empty<IPEndPoint>();
        private string _upstreamDescription = "Local / Corporate DNS";
        private Socket? _listenerSocket;
        private Socket? _tcpListenerSocket;
        private CancellationTokenSource? _cts;
        private volatile bool _isRunning;
        private bool _networkEventsSubscribed;
        private long _totalQueries;
        private long _cacheHits;

        public bool IsRunning => _isRunning;
        public bool IsTcpRelayActive { get; private set; }
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
            string relayType = IsTcpRelayActive ? "UDP+TCP" : "UDP";
            return $"⚡ DNS Cache: {hits} hits / {queries} queries ({hitRate:F0}%, {_cache.Count} cached, {relayType}) • {_upstreamDescription}";
        }

        public event Action<string>? OnLog;

        /// <summary>
        /// Detects active upstream DNS servers configured on physical/primary host network adapters
        /// (e.g. corporate domain controllers, enterprise security agents on 127.0.0.1, or ISP routers),
        /// ensuring corporate intranet names resolve properly and avoiding blocks on public Anycast IPs.
        /// </summary>
        public static List<IPEndPoint> GetLocalDnsServers()
        {
            var servers = new List<IPEndPoint>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;

                    // Exclude SoftAP virtual adapters
                    string desc = nic.Description ?? "";
                    string name = nic.Name ?? "";
                    if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Direct", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Local Area Connection*")) continue;

                    var props = nic.GetIPProperties();
                    foreach (var dns in props.DnsAddresses)
                    {
                        // Allow IPv4 addresses including 127.0.0.1 (used by corporate security agents like Zscaler/Netskope or local Pi-hole)
                        // Only exclude the hotspot's own subnet (192.168.137.x) to prevent forwarding loops
                        if (dns.AddressFamily == AddressFamily.InterNetwork &&
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

        public void RefreshUpstreamServers()
        {
            var local = GetLocalDnsServers();
            _localUpstreamServers = local.ToArray();
            if (_localUpstreamServers.Length > 0)
            {
                bool hasLoopback = _localUpstreamServers.Any(s => IPAddress.IsLoopback(s.Address));
                string serverIps = string.Join(", ", _localUpstreamServers.Take(2).Select(s => s.Address.ToString()));
                _upstreamDescription = hasLoopback
                    ? $"Local Stub Resolver ({serverIps})"
                    : $"Local/Corporate DNS ({serverIps})";
            }
            else
            {
                _upstreamDescription = "No Local DNS (Anycast 1.1.1.1 / 8.8.8.8)";
            }
        }

        private void OnNetworkChanged(object? sender, EventArgs e)
        {
            try
            {
                if (_isRunning)
                {
                    RefreshUpstreamServers();
                    OnLog?.Invoke($"[DNS-Cache] Network change detected. Active upstream: {_upstreamDescription}");
                }
            }
            catch { }
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
                RefreshUpstreamServers();

                if (!_networkEventsSubscribed)
                {
                    NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                    NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
                    _networkEventsSubscribed = true;
                }

                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                // 1. UDP Listener on port 53
                _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _listenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                // Never bind to IPAddress.Any (prevents open-resolver vulnerability)
                _listenerSocket.Bind(new IPEndPoint(listenAddress, 53));
                _isRunning = true;
                LastError = null;

                // 2. TCP Listener on port 53 (handles truncated query fallback & DNSSEC lookups)
                IsTcpRelayActive = StartTcpListener(listenAddress, token);

                string relayStatus = IsTcpRelayActive ? "UDP+TCP Relay" : "UDP only";
                OnLog?.Invoke($"[DNS-Cache] Zero-Latency DNS Cache active on {listenAddress}:53 ({relayStatus}) • Upstream: {_upstreamDescription}");

                Task.Run(() => ListenLoopAsync(_listenerSocket, token), token);

                return true;
            }
            catch (Exception ex)
            {
                _isRunning = false;
                IsTcpRelayActive = false;
                LastError = "Port 53 in use by Windows ICS";

                if (_networkEventsSubscribed)
                {
                    try
                    {
                        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
                        _networkEventsSubscribed = false;
                    }
                    catch { }
                }

                try
                {
                    _listenerSocket?.Close();
                    _listenerSocket?.Dispose();
                    _listenerSocket = null;
                }
                catch { }

                try
                {
                    _tcpListenerSocket?.Close();
                    _tcpListenerSocket?.Dispose();
                    _tcpListenerSocket = null;
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

        private bool StartTcpListener(IPAddress listenAddress, CancellationToken token)
        {
            try
            {
                _tcpListenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _tcpListenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _tcpListenerSocket.Bind(new IPEndPoint(listenAddress, 53));
                _tcpListenerSocket.Listen(16);
                Task.Run(() => TcpListenLoopAsync(_tcpListenerSocket, token), token);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TCP DNS listener bind skipped: {ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            IsTcpRelayActive = false;

            if (_networkEventsSubscribed)
            {
                try
                {
                    NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                    NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
                    _networkEventsSubscribed = false;
                }
                catch { }
            }

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

            try
            {
                _tcpListenerSocket?.Close();
                _tcpListenerSocket?.Dispose();
                _tcpListenerSocket = null;
            }
            catch { }

            OnLog?.Invoke("[DNS-Cache] DNS Cache service stopped.");
        }

        private async Task ListenLoopAsync(Socket socket, CancellationToken token)
        {
            byte[] buffer = new byte[4096];
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
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeoutCts.CancelAfter(3500); // Strict per-query handler deadline

                        bool acquired = false;
                        try
                        {
                            acquired = await _concurrencyLimiter.WaitAsync(1000, timeoutCts.Token).ConfigureAwait(false);
                            if (acquired)
                            {
                                await HandleQueryAsync(socket, queryBytes, clientEp, timeoutCts.Token).ConfigureAwait(false);
                            }
                        }
                        catch { }
                        finally
                        {
                            if (acquired)
                            {
                                _concurrencyLimiter.Release();
                            }
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

        private async Task TcpListenLoopAsync(Socket listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var clientSocket = await listener.AcceptAsync(token).ConfigureAwait(false);
                    _ = Task.Run(async () =>
                    {
                        using (clientSocket)
                        {
                            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            connCts.CancelAfter(3000); // 3s per-connection deadline

                            using var reg = connCts.Token.Register(() => { try { clientSocket.Close(); clientSocket.Dispose(); } catch { } });

                            bool acquired = false;
                            try
                            {
                                acquired = await _concurrencyLimiter.WaitAsync(1000, connCts.Token).ConfigureAwait(false);
                                if (!acquired) return;

                                // Read 2-byte length prefix
                                byte[] lenBuf = new byte[2];
                                int read = await ReceiveExactAsync(clientSocket, lenBuf, 0, 2, connCts.Token).ConfigureAwait(false);
                                if (read < 2) return;
                                int queryLen = (lenBuf[0] << 8) | lenBuf[1];
                                if (queryLen <= 12 || queryLen > 4096) return;

                                byte[] queryBytes = new byte[queryLen];
                                read = await ReceiveExactAsync(clientSocket, queryBytes, 0, queryLen, connCts.Token).ConfigureAwait(false);
                                if (read < queryLen) return;

                                Interlocked.Increment(ref _totalQueries);

                                var parsed = ParseDnsQuery(queryBytes);
                                byte[]? response = null;

                                // 1. Check in-memory cache
                                if (!string.IsNullOrEmpty(parsed.CacheKey) && _cache.TryGetValue(parsed.CacheKey, out var entry))
                                {
                                    if (DateTime.UtcNow < entry.ExpiresAt)
                                    {
                                        Interlocked.Increment(ref _cacheHits);
                                        response = (byte[])entry.Response.Clone();
                                        response[0] = (byte)(parsed.QueryId >> 8);
                                        response[1] = (byte)(parsed.QueryId & 0xFF);

                                        // Splice question section so response carries client's exact casing (0x20 compliant)
                                        if (parsed.QuestionLength > 0 && response.Length >= 12 + parsed.QuestionLength)
                                        {
                                            Buffer.BlockCopy(queryBytes, 12, response, 12, parsed.QuestionLength);
                                        }
                                    }
                                }

                                // 2. Upstream forward if cache miss
                                if (response == null)
                                {
                                    var (fwdResp, isLocal) = await ForwardQueryAsync(queryBytes, connCts.Token).ConfigureAwait(false);
                                    if (fwdResp != null && fwdResp.Length >= 12)
                                    {
                                        fwdResp[0] = (byte)(parsed.QueryId >> 8);
                                        fwdResp[1] = (byte)(parsed.QueryId & 0xFF);
                                        response = fwdResp;

                                        int rcode = response[3] & 0x0F;
                                        bool isTruncated = (response[2] & 0x02) != 0;
                                        if (!string.IsNullOrEmpty(parsed.CacheKey) && !isTruncated)
                                        {
                                            if (_cache.Count >= MaxCacheEntries)
                                            {
                                                PruneExpiredCache();
                                            }

                                            if (_cache.Count < MaxCacheEntries)
                                            {
                                                if (rcode == 0)
                                                {
                                                    uint ttl = ExtractTtl(response);
                                                    uint maxTtl = isLocal ? 3600u : 60u;
                                                    var expiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(ttl, 15, maxTtl));
                                                    _cache[parsed.CacheKey] = (response, expiresAt, isLocal);
                                                }
                                                else if (rcode == 3)
                                                {
                                                    var expiresAt = DateTime.UtcNow.AddSeconds(30);
                                                    _cache[parsed.CacheKey] = (response, expiresAt, isLocal);
                                                }
                                            }
                                        }
                                    }
                                }

                                // 3. Synthesize SERVFAIL if all upstreams failed
                                response ??= SynthesizeServFail(queryBytes);

                                byte[] respLenBuf = new byte[2] { (byte)(response.Length >> 8), (byte)(response.Length & 0xFF) };
                                await clientSocket.SendAsync(respLenBuf.AsMemory(), SocketFlags.None, connCts.Token).ConfigureAwait(false);
                                await clientSocket.SendAsync(response.AsMemory(), SocketFlags.None, connCts.Token).ConfigureAwait(false);
                            }
                            catch { }
                            finally
                            {
                                if (acquired)
                                {
                                    _concurrencyLimiter.Release();
                                }
                            }
                        }
                    });
                }
                catch (OperationCanceledException) { break; }
                catch when (!_isRunning) { break; }
                catch { }
            }
        }

        private static async Task<int> ReceiveExactAsync(Socket socket, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int total = 0;
            while (total < count)
            {
                int bytes = await socket.ReceiveAsync(buffer.AsMemory(offset + total, count - total), SocketFlags.None, token).ConfigureAwait(false);
                if (bytes == 0) break;
                total += bytes;
            }
            return total;
        }

        private async Task HandleQueryAsync(Socket socket, byte[] query, EndPoint clientEp, CancellationToken token)
        {
            try
            {
                var parsed = ParseDnsQuery(query);

                // 1. Check local in-memory cache
                if (!string.IsNullOrEmpty(parsed.CacheKey) && _cache.TryGetValue(parsed.CacheKey, out var entry))
                {
                    if (DateTime.UtcNow < entry.ExpiresAt)
                    {
                        Interlocked.Increment(ref _cacheHits);
                        byte[] cachedResponse = (byte[])entry.Response.Clone();
                        cachedResponse[0] = (byte)(parsed.QueryId >> 8);
                        cachedResponse[1] = (byte)(parsed.QueryId & 0xFF);

                        // Splice question section so response carries client's exact casing (0x20 compliant)
                        if (parsed.QuestionLength > 0 && cachedResponse.Length >= 12 + parsed.QuestionLength)
                        {
                            Buffer.BlockCopy(query, 12, cachedResponse, 12, parsed.QuestionLength);
                        }

                        // Enforce client's EDNS buffer size over UDP: set TC bit if response exceeds payload limit
                        if (cachedResponse.Length > parsed.UdpPayloadSize && parsed.QuestionLength > 0 && cachedResponse.Length >= 12 + parsed.QuestionLength)
                        {
                            int optRecordLen = parsed.OptRecordBytes?.Length ?? 0;
                            byte[] tcResponse = new byte[12 + parsed.QuestionLength + optRecordLen];
                            Buffer.BlockCopy(cachedResponse, 0, tcResponse, 0, 12 + parsed.QuestionLength);
                            tcResponse[2] |= 0x02; // Set TC bit
                            tcResponse[6] = 0; tcResponse[7] = 0; // ANCOUNT = 0
                            tcResponse[8] = 0; tcResponse[9] = 0; // NSCOUNT = 0
                            if (optRecordLen > 0 && parsed.OptRecordBytes != null)
                            {
                                tcResponse[10] = 0; tcResponse[11] = 1; // ARCOUNT = 1 (preserve client's OPT record)
                                Buffer.BlockCopy(parsed.OptRecordBytes, 0, tcResponse, 12 + parsed.QuestionLength, optRecordLen);
                            }
                            else
                            {
                                tcResponse[10] = 0; tcResponse[11] = 0; // ARCOUNT = 0
                            }
                            cachedResponse = tcResponse;
                        }

                        await socket.SendToAsync(new ArraySegment<byte>(cachedResponse), SocketFlags.None, clientEp).ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        _cache.TryRemove(parsed.CacheKey, out _);
                    }
                }

                // 2. Upstream Resolution (Local/Corporate DNS first with Anycast fallback)
                var (upstreamResponse, isLocal) = await ForwardQueryAsync(query, token).ConfigureAwait(false);
                if (upstreamResponse != null && upstreamResponse.Length >= 12)
                {
                    upstreamResponse[0] = (byte)(parsed.QueryId >> 8);
                    upstreamResponse[1] = (byte)(parsed.QueryId & 0xFF);

                    int rcode = upstreamResponse[3] & 0x0F;
                    bool isTruncated = (upstreamResponse[2] & 0x02) != 0;

                    if (!string.IsNullOrEmpty(parsed.CacheKey) && !isTruncated)
                    {
                        if (_cache.Count >= MaxCacheEntries)
                        {
                            PruneExpiredCache();
                        }

                        if (_cache.Count < MaxCacheEntries)
                        {
                            if (rcode == 0) // NoError
                            {
                                uint ttl = ExtractTtl(upstreamResponse);
                                // If answered by public anycast when local servers are present, cap TTL to 60s
                                uint maxTtl = isLocal ? 3600u : 60u;
                                var expiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(ttl, 15, maxTtl));
                                _cache[parsed.CacheKey] = (upstreamResponse, expiresAt, isLocal);
                            }
                            else if (rcode == 3) // NXDOMAIN: 30s negative caching
                            {
                                var expiresAt = DateTime.UtcNow.AddSeconds(30);
                                _cache[parsed.CacheKey] = (upstreamResponse, expiresAt, isLocal);
                            }
                        }
                    }

                    // Send verified answer to client
                    await socket.SendToAsync(new ArraySegment<byte>(upstreamResponse), SocketFlags.None, clientEp).ConfigureAwait(false);
                }
                else
                {
                    // 3. Synthesize SERVFAIL when every upstream times out or fails
                    // Prevents client resolvers from hanging for 2-5 seconds waiting for UDP timeout
                    byte[] servFail = SynthesizeServFail(query);
                    await socket.SendToAsync(new ArraySegment<byte>(servFail), SocketFlags.None, clientEp).ConfigureAwait(false);
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

            // If still at or near capacity, prune 20% by earliest-expiry sample
            if (_cache.Count >= MaxCacheEntries)
            {
                int toRemove = MaxCacheEntries / 5;
                // Sample 2x target items to find earliest-expiring records without sorting the entire 50k collection
                var earliest = _cache.Take(toRemove * 2)
                                     .OrderBy(kvp => kvp.Value.ExpiresAt)
                                     .Take(toRemove)
                                     .Select(kvp => kvp.Key)
                                     .ToList();
                foreach (var key in earliest)
                {
                    _cache.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Deterministic DNS Resolution:
        /// 1. Queries Local/Corporate DNS servers in parallel with a genuine 350ms window.
        ///    If a local server answers with NoError (0) or authoritative NXDOMAIN (3), it is immediately returned,
        ///    protecting enterprise split-horizon hostnames from public anycast poisoning.
        /// 2. If local servers return SERVFAIL (2), REFUSED (5), time out, or fail, queries Anycast resolvers (1.1.1.1 / 8.8.8.8).
        /// </summary>
        private async Task<(byte[]? Response, bool IsLocal)> ForwardQueryAsync(byte[] query, CancellationToken token)
        {
            var localServers = _localUpstreamServers;
            if (localServers != null && localServers.Length > 0)
            {
                // Local / Corporate DNS first with strict ~350ms grace window
                using var localCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                localCts.CancelAfter(350);

                var localTasks = new List<Task<byte[]?>>(localServers.Length);
                for (int i = 0; i < localServers.Length; i++)
                {
                    localTasks.Add(QuerySingleUpstreamAsync(query, localServers[i], localCts.Token));
                }

                byte[]? localFallback = null;
                while (localTasks.Count > 0)
                {
                    var completed = await Task.WhenAny(localTasks).ConfigureAwait(false);
                    localTasks.Remove(completed);

                    try
                    {
                        byte[]? res = await completed.ConfigureAwait(false);
                        if (res != null && res.Length >= 12)
                        {
                            int rcode = res[3] & 0x0F;
                            if (rcode == 0) // Clean NoError answer: local split-horizon wins
                            {
                                try { localCts.Cancel(); } catch { }
                                return (res, true);
                            }
                            if (rcode == 3) // NXDOMAIN: authoritative negative response for intranet
                            {
                                localFallback ??= res;
                            }
                            // Note: RCODE 2 (SERVFAIL) and 5 (REFUSED) from corporate DNS are NOT authoritative
                            // for external domains, so we do NOT save them to localFallback and allow fallback to Anycast!
                        }
                    }
                    catch { }
                }

                // If local returned an authoritative NXDOMAIN for an internal domain, respect it
                if (localFallback != null)
                {
                    return (localFallback, true);
                }
            }

            // Fallback to Anycast resolvers (Cloudflare 1.1.1.1 / Google 8.8.8.8) with strict 2s deadline
            using var anycastCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            anycastCts.CancelAfter(2000);

            var anycastTasks = new List<Task<byte[]?>>(DefaultAnycastDnsServers.Length);
            for (int i = 0; i < DefaultAnycastDnsServers.Length; i++)
            {
                anycastTasks.Add(QuerySingleUpstreamAsync(query, DefaultAnycastDnsServers[i], anycastCts.Token));
            }

            byte[]? anycastFallback = null;
            while (anycastTasks.Count > 0)
            {
                var completed = await Task.WhenAny(anycastTasks).ConfigureAwait(false);
                anycastTasks.Remove(completed);

                try
                {
                    byte[]? res = await completed.ConfigureAwait(false);
                    if (res != null && res.Length >= 12)
                    {
                        int rcode = res[3] & 0x0F;
                        if (rcode == 0)
                        {
                            try { anycastCts.Cancel(); } catch { }
                            return (res, false);
                        }
                        anycastFallback ??= res;
                    }
                }
                catch { }
            }

            return (anycastFallback, false);
        }

        private static async Task<byte[]?> QuerySingleUpstreamAsync(byte[] query, IPEndPoint server, CancellationToken token)
        {
            try
            {
                using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // Register token cancellation to instantly close and unblock pending socket I/O
                using var reg = token.Register(() => { try { client.Close(); client.Dispose(); } catch { } });

                client.Connect(server);

                await client.SendAsync(query.AsMemory(), SocketFlags.None, token).ConfigureAwait(false);

                byte[] receiveBuf = new byte[4096];
                int res = await client.ReceiveAsync(receiveBuf.AsMemory(), SocketFlags.None, token).ConfigureAwait(false);

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
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch { }
            return null;
        }

        private static int GetQuestionSectionLength(byte[] query)
        {
            if (query.Length <= 12) return query.Length;
            try
            {
                int qdCount = (query[4] << 8) | query[5];
                if (qdCount <= 0) return 12;

                int pos = 12;
                for (int q = 0; q < qdCount && pos < query.Length; q++)
                {
                    // Parse QNAME
                    while (pos < query.Length)
                    {
                        byte len = query[pos++];
                        if (len == 0) break; // Terminating zero
                        if ((len & 0xC0) == 0xC0)
                        {
                            // Pointer: 1 more byte
                            pos++;
                            break;
                        }
                        pos += len;
                    }
                    // QTYPE (2 bytes) + QCLASS (2 bytes)
                    pos += 4;
                }
                return Math.Min(pos, query.Length);
            }
            catch
            {
                return Math.Min(query.Length, 512);
            }
        }

        private static byte[] SynthesizeServFail(byte[] query)
        {
            int qLen = GetQuestionSectionLength(query);
            byte[] resp = new byte[Math.Max(12, qLen)];
            if (query.Length >= 12)
            {
                Array.Copy(query, 0, resp, 0, qLen);
            }
            else
            {
                resp[0] = query.Length > 0 ? query[0] : (byte)0;
                resp[1] = query.Length > 1 ? query[1] : (byte)0;
            }

            // Header Flags:
            // QR = 1, Opcode = copy from query, AA = 0, TC = 0, RD = copy from query
            byte qFlagsHi = query.Length > 2 ? query[2] : (byte)0x01; // Preserve RD bit
            resp[2] = (byte)(0x80 | (qFlagsHi & 0x79)); // QR=1, preserve opcode & RD
            resp[3] = 0x82; // RA=1, RCODE=2 (SERVFAIL)

            // ANCOUNT=0, NSCOUNT=0, ARCOUNT=0
            resp[6] = 0; resp[7] = 0;
            resp[8] = 0; resp[9] = 0;
            resp[10] = 0; resp[11] = 0;

            return resp;
        }

        private readonly record struct ParsedDnsQuery(
            ushort QueryId,
            string CacheKey,
            int QuestionLength,
            bool IsDo,
            bool IsCd,
            ushort UdpPayloadSize,
            byte[]? OptRecordBytes
        );

        private static ParsedDnsQuery ParseDnsQuery(byte[] query)
        {
            if (query.Length <= 12) return new ParsedDnsQuery(0, string.Empty, 0, false, false, 512, null);
            try
            {
                ushort queryId = (ushort)((query[0] << 8) | query[1]);
                bool isCd = (query[3] & 0x10) != 0;
                ushort arCount = (ushort)((query[10] << 8) | query[11]);

                int pos = 12;
                var sb = new StringBuilder(64);
                while (pos < query.Length)
                {
                    byte len = query[pos++];
                    if (len == 0) break;
                    if ((len & 0xC0) == 0xC0) { pos++; break; } // Pointer in question -> skip caching
                    if (pos + len > query.Length) return new ParsedDnsQuery(queryId, string.Empty, 0, false, isCd, 512, null);
                    for (int i = 0; i < len; i++)
                    {
                        byte b = query[pos++];
                        sb.Append((char)(b >= 'A' && b <= 'Z' ? b + 32 : b));
                    }
                    sb.Append('.');
                }

                if (pos + 4 > query.Length) return new ParsedDnsQuery(queryId, string.Empty, 0, false, isCd, 512, null);

                ushort qtype = (ushort)((query[pos] << 8) | query[pos + 1]);
                ushort qclass = (ushort)((query[pos + 2] << 8) | query[pos + 3]);
                pos += 4;
                int questionLength = pos - 12;

                bool hasOpt = false;
                byte ednsVersion = 0;
                bool isDo = false;
                ushort udpPayloadSize = 512;
                string optData = string.Empty;
                byte[]? optBytes = null;

                if (arCount > 0 && pos < query.Length)
                {
                    // Scan additional records for OPT RR (Type 41 / EDNS0)
                    int optStart = pos;
                    if (query[pos] == 0 && pos + 11 <= query.Length)
                    {
                        pos++; // root domain (0)
                        ushort rrType = (ushort)((query[pos] << 8) | query[pos + 1]);
                        if (rrType == 41) // OPT
                        {
                            hasOpt = true;
                            udpPayloadSize = (ushort)((query[pos + 2] << 8) | query[pos + 3]);
                            ednsVersion = query[pos + 5];
                            isDo = (query[pos + 6] & 0x80) != 0; // DO bit (DNSSEC OK)
                            ushort rdLength = (ushort)((query[pos + 8] << 8) | query[pos + 9]);
                            if (pos + 10 + rdLength <= query.Length)
                            {
                                int optTotalLen = 1 + 10 + rdLength;
                                optBytes = new byte[optTotalLen];
                                Buffer.BlockCopy(query, optStart, optBytes, 0, optTotalLen);
                                if (rdLength > 0)
                                {
                                    optData = Convert.ToBase64String(query, pos + 10, rdLength);
                                }
                            }
                        }
                    }
                }

                sb.Append(':').Append(qtype).Append(':').Append(qclass);
                if (hasOpt)
                {
                    sb.Append(":edns=1&v=").Append(ednsVersion)
                      .Append(":do=").Append(isDo ? 1 : 0);
                    if (!string.IsNullOrEmpty(optData))
                    {
                        sb.Append("&opt=").Append(optData);
                    }
                }
                else
                {
                    sb.Append(":edns=0:do=0");
                }
                sb.Append(":cd=").Append(isCd ? 1 : 0);

                return new ParsedDnsQuery(queryId, sb.ToString(), questionLength, isDo, isCd, Math.Max((ushort)512, udpPayloadSize), optBytes);
            }
            catch
            {
                ushort queryId = query.Length >= 2 ? (ushort)((query[0] << 8) | query[1]) : (ushort)0;
                return new ParsedDnsQuery(queryId, string.Empty, 0, false, false, 512, null);
            }
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
