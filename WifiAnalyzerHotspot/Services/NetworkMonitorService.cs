using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class NetworkMonitorService
    {
        private long _lastBytesReceived = -1;
        private long _lastBytesSent = -1;
        private DateTime _lastTime = DateTime.UtcNow;

        public LiveTrafficPoint GetCurrentTraffic()
        {
            long currentBytesRecv = 0;
            long currentBytesSent = 0;

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus == OperationalStatus.Up &&
                        (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                         ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
                    {
                        var stats = ni.GetIPv4Statistics();
                        currentBytesRecv += stats.BytesReceived;
                        currentBytesSent += stats.BytesSent;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading stats: {ex.Message}");
            }

            var now = DateTime.UtcNow;
            double elapsedSec = (now - _lastTime).TotalSeconds;

            double rxBps = 0;
            double txBps = 0;

            if (_lastBytesReceived >= 0 && elapsedSec > 0.1)
            {
                rxBps = Math.Max(0, (currentBytesRecv - _lastBytesReceived) / elapsedSec);
                txBps = Math.Max(0, (currentBytesSent - _lastBytesSent) / elapsedSec);
            }

            _lastBytesReceived = currentBytesRecv;
            _lastBytesSent = currentBytesSent;
            _lastTime = now;

            return new LiveTrafficPoint
            {
                DownloadBps = rxBps,
                UploadBps = txBps,
                DownloadFormatted = FormatBytes(rxBps),
                UploadFormatted = FormatBytes(txBps),
                Timestamp = DateTime.Now
            };
        }

        private static string FormatBytes(double bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024)
            {
                return $"{(bytesPerSec / (1024 * 1024)):F2} MB/s";
            }
            if (bytesPerSec >= 1024)
            {
                return $"{(bytesPerSec / 1024):F1} KB/s";
            }
            return $"{bytesPerSec:F0} B/s";
        }
    }
}
