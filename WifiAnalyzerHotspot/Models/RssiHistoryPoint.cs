using System;

namespace WifiAnalyzerHotspot.Models
{
    public class RssiHistoryPoint
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public int RssiDbm { get; set; }
        public int SignalPercent { get; set; }
        public string Ssid { get; set; } = string.Empty;

        public RssiHistoryPoint() { }

        public RssiHistoryPoint(int rssiDbm, int signalPercent, string ssid)
        {
            Timestamp = DateTime.Now;
            RssiDbm = rssiDbm;
            SignalPercent = signalPercent;
            Ssid = ssid;
        }
    }
}
