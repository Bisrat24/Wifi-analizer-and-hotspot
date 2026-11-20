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
                // Header (qd=1, an=1) + root question + answer with TTL=42 and 4-byte RDATA.
                var response = new byte[33];
                response[2] = 0x81; response[3] = 0x80;
                response[5] = 1; response[7] = 1;
                response[12] = 0; // root name
                response[13] = 0; response[14] = 1; // QTYPE A
                response[15] = 0; response[16] = 1; // QCLASS IN
                response[17] = 0xC0; response[18] = 0x0C; // compressed name pointer to byte 12
                response[19] = 0; response[20] = 1; // TYPE A
                response[21] = 0; response[22] = 1; // CLASS IN
                response[26] = 42; // TTL low byte (42)
                response[27] = 0; response[28] = 4; // RDLENGTH 4
                response[29] = 1; response[30] = 2; response[31] = 3; response[32] = 4; // 1.2.3.4
                Expect(HotspotDnsCacheService.ExtractTtl(response) == 42, "ttl mismatch");
            }, failures);

            Check("PowerShell escaping", () =>
            {
                Expect(SystemRestoreService.Escape("O'Brien") == "O''Brien", "quote not doubled");
            }, failures);

            Check("Secure Connect profile XML", () =>
            {
                string xml = SecureConnectService.BuildProfileXml("TestNet", "password123", "WPA3-Personal", "AES");
                Expect(xml.Contains("<authentication>WPA3SAE</authentication>"), "WPA3 profile wrong");
                Expect(xml.Contains("transitionMode"), "WPA3 profile needs transitionMode");
                Expect(xml.Contains("xmlns=\"https://www.microsoft.com/networking/WLAN/profile/v1\""), "profile namespace must be https");
                Expect(xml.Contains("<autoSwitch>false</autoSwitch>"), "autoSwitch must be false for manual profile");
                Expect(xml.Contains("<connectionMode>manual</connectionMode>"), "profile should not auto-connect in Windows");
                string open = SecureConnectService.BuildProfileXml("Guest", "", "Open", "None");
                Expect(open.Contains("<authentication>open</authentication>"), "open profile wrong");
            }, failures);

            Check("Secure Connect SSID dedupe", () =>
            {
                var list = new List<WifiNetwork>
                {
                    new WifiNetwork { Ssid = "MeshNet", Bssid = "AA", SignalPercent = 40 },
                    new WifiNetwork { Ssid = "MeshNet", Bssid = "BB", SignalPercent = 80 },
                    new WifiNetwork { Ssid = "Other", Bssid = "CC", SignalPercent = 50 }
                };
                var deduped = ViewModels.MainViewModel.DedupeNetworksByName(list);
                Expect(deduped.Count == 2, $"expected 2 networks, got {deduped.Count}");
                Expect(deduped[0].SignalPercent == 80, "strongest BSSID not kept");
            }, failures);

            Check("Wi-Fi QR payload formatting", () =>
            {
                string payload = QrCodeService.FormatWifiPayload(@"Net;Work\1", "P@ss:w;ord", "WPA");
                Expect(payload.Contains(@"S:Net\;Work\\1;"), "SSID not escaped");
                Expect(payload.Contains(@"P:P@ss\:w\;ord;"), "Password not escaped");
                string openPayload = QrCodeService.FormatWifiPayload("Guest", "", "Open");
                Expect(openPayload == "WIFI:T:nopass;S:Guest;;", $"open payload wrong: {openPayload}");
            }, failures);

            Check("QR matrix and bitmap generation", () =>
            {
                string text = "WIFI:T:WPA;S:TestHotspot;P:12345678;;";
                var matrix = QrCodeService.GenerateMatrix(text);
                Expect(matrix != null, "matrix was null");
                int dim = matrix!.GetLength(0);
                Expect(dim >= 21 && dim <= 45, $"unexpected dimension {dim}");
                // Verify finder pattern corners at (0,0)
                Expect(matrix[0, 0] && matrix[0, 6] && matrix[6, 0] && matrix[6, 6], "finder pattern corners must be dark");
                Expect(!matrix[1, 1] && !matrix[1, 5] && !matrix[5, 1] && !matrix[5, 5], "finder inner ring must be light");
                Expect(matrix[2, 2] && matrix[3, 3] && matrix[4, 4], "finder center must be dark");

                // Version 7: verify format copies, dark module and version information.
                var v7 = QrCodeService.GenerateMatrix(new string('A', 110));
                Expect(v7 != null && v7.GetLength(0) == 45, "version 7 matrix expected");
                int v7dim = v7!.GetLength(0);
                Expect(v7[v7dim - 8, 8], "dark module must be dark");
                Expect(v7[8, v7dim - 2], "top-right format bit 1 must be dark");
                Expect(!v7[v7dim - 7, 8], "bottom-left format bit 8 must be light (0x5412)");
                Expect(v7[v7dim - 5, 8], "bottom-left format bit 10 must be dark");
                Expect(v7[v7dim - 1, 8], "bottom-left format bit 14 must be dark");
                Expect(v7[0, v7dim - 9], "version 7 info bit 2 must be dark");
            }, failures);

            Check("Reed-Solomon syndrome is zero", () =>
            {
                byte[] data = System.Text.Encoding.ASCII.GetBytes("0123456789ABCDEF");
                byte[] ec = QrCodeService.CalculateReedSolomon(data, 10);
                Expect(ec.Length == 10, "ec length");
                var code = new byte[data.Length + ec.Length];
                Array.Copy(data, code, data.Length);
                Array.Copy(ec, 0, code, data.Length, ec.Length);

                int alpha = 1;
                for (int i = 0; i < 10; i++)
                {
                    int sum = 0;
                    foreach (byte c in code) sum = GfMul(sum, alpha) ^ c;
                    Expect(sum == 0, $"syndrome {i} = {sum}");
                    alpha = GfMul(alpha, 2);
                }
            }, failures);

            string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WifiAnalyzerHotspot_selfcheck.log");
            if (failures.Count == 0)
            {
                System.IO.File.WriteAllText(logPath, "[SelfCheck] all checks passed");
                Debug.WriteLine("[SelfCheck] all checks passed");
                return true;
            }
            foreach (string failure in failures) Debug.WriteLine($"[SelfCheck] FAILED: {failure}");
            System.IO.File.WriteAllText(logPath, string.Join(Environment.NewLine, failures));
            return false;
        }

        private static int GfMul(int a, int b)
        {
            int result = 0;
            while (b != 0)
            {
                if ((b & 1) != 0) result ^= a;
                bool high = (a & 0x80) != 0;
                a = (a << 1) & 0xFF;
                if (high) a ^= 0x1D;
                b >>= 1;
            }
            return result;
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
