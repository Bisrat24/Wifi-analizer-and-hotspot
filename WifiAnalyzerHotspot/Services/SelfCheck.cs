using System;
using System.Collections.Generic;
using System.Diagnostics;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Dependency-free assertions for pure logic. Invoked via --selfcheck at startup.
    /// </summary>
    internal static class SelfCheck
    {
        internal static bool RunAll()
        {
            var failures = new List<string>();

            Check("dual speed 100,20", () =>
            {
                var model = new HotspotModel { BufferbloatLineSpeed = "100,20" };
                var (down, up) = model.GetBufferbloatThrottleBits();
                Expect(down == 90_000_000, $"down={down}");
                Expect(up == 18_000_000, $"up={up}");
            }, failures);

            Check("thousands separator 1,000/20", () =>
            {
                var model = new HotspotModel { BufferbloatLineSpeed = "1,000/20" };
                var (down, up) = model.GetBufferbloatThrottleBits();
                Expect(down == 900_000_000, $"down={down}");
                Expect(up == 18_000_000, $"up={up}");
            }, failures);

            Check("keepalive backoff", () =>
            {
                var scheduler = new KeepAliveScheduler();
                scheduler.Record(false);
                Expect(scheduler.NextDelayMs(1) >= 1000, "backoff must not be shorter than base interval");
                scheduler.Record(true);
                int normal = scheduler.NextDelayMs(1);
                Expect(normal >= 900 && normal <= 1100, $"normal={normal}");
            }, failures);

            Check("DNS TTL extraction", () =>
            {
                // Header (qd=1, an=1) + root question + answer with TTL=42.
                var response = new byte[27];
                response[2] = 0x81; response[3] = 0x80;
                response[5] = 1; response[7] = 1;
                response[12] = 0; // root name
                response[16] = 0; response[17] = 1; // QTYPE A
                response[18] = 0; response[19] = 1; // QCLASS IN
                response[20] = 0xC0; response[21] = 0x0C; // compressed name
                response[22] = 0; response[23] = 1; // TYPE A
                response[24] = 0; response[25] = 1; // CLASS IN
                response[26] = 42; // TTL low byte (42)
                Expect(HotspotDnsCacheService.ExtractTtl(response) == 42, "ttl mismatch");
            }, failures);

            Check("PowerShell escaping", () =>
            {
                Expect(SystemRestoreService.Escape("O'Brien") == "O''Brien", "quote not doubled");
            }, failures);

            if (failures.Count == 0)
            {
                Debug.WriteLine("[SelfCheck] all checks passed");
                return true;
            }
            foreach (string failure in failures) Debug.WriteLine($"[SelfCheck] FAILED: {failure}");
            return false;
        }

        private static void Check(string name, Action body, List<string> failures)
        {
            try { body(); }
            catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
        }

        private static void Expect(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
    }
}
