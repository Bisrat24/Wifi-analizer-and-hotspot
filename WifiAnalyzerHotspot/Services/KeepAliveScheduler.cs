using System;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Pure timing policy for the keep-alive pulse: small jitter avoids synchronized
    /// traffic bursts; failed probes back off exponentially and recover on first success.
    /// </summary>
    internal sealed class KeepAliveScheduler
    {
        private readonly Random _random = new();
        private int _consecutiveFailures;

        public int FailureCount => _consecutiveFailures;

        public int NextDelayMs(int baseIntervalSeconds)
        {
            int baseMs = Math.Max(250, baseIntervalSeconds * 1000);
            int jitterRange = Math.Max(10, baseMs / 10);
            int delay = baseMs + _random.Next(-jitterRange, jitterRange + 1);

            if (_consecutiveFailures > 0)
            {
                int multiplier = 1 << Math.Min(_consecutiveFailures, 3);
                delay = Math.Min(baseMs * 8, baseMs * multiplier);
            }
            return Math.Max(200, delay);
        }

        public void Record(bool success)
        {
            _consecutiveFailures = success ? 0 : Math.Min(10, _consecutiveFailures + 1);
        }
    }
}
