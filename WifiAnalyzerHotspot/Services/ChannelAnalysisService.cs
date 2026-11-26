using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class ChannelAnalysisService
    {
        private static readonly int[] Standard24GChannels = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 };
        private static readonly int[] Standard5GChannels = new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 };
        public static readonly int[] Standard6GChannels = new[] { 1, 5, 21, 37, 53, 69, 85, 101, 117, 133, 149, 165, 181, 197, 213, 229, 233 };
        public static readonly HashSet<int> PreferredScanningChannels6G = new() { 5, 21, 37, 53, 69, 85, 101, 117, 133, 149, 165, 181, 197, 213, 229 };

        /// <summary>
        /// Analyzes 2.4 GHz channels and calculates congestion, co-channel & adjacent interference, and ratings.
        /// </summary>
        public List<ChannelRating> Analyze24GChannels(IEnumerable<WifiNetwork> networks)
        {
            var netList = networks.Where(n => n.Band.Contains("2.4")).ToList();
            var ratings = new List<ChannelRating>();

            foreach (var ch in Standard24GChannels)
            {
                int coChannel = 0;
                int overlap = 0;
                double penalty = 0;

                foreach (var net in netList)
                {
                    int diff = Math.Abs(net.Channel - ch);
                    double weight = Math.Max(0.1, (net.SignalPercent / 100.0));

                    if (diff == 0)
                    {
                        coChannel++;
                        penalty += 25 * weight;
                    }
                    else if (diff <= 4)
                    {
                        overlap++;
                        // Adjacent interference is worse for closer channels
                        penalty += (20 - (diff * 4)) * weight;
                    }
                }

                int score = Math.Max(0, (int)Math.Round(100 - penalty));
                var rating = new ChannelRating
                {
                    Channel = ch,
                    Band = "2.4 GHz",
                    Score = score,
                    CoChannelCount = coChannel,
                    OverlapCount = overlap
                };

                // Assign stars and text
                FormatRating(rating, isPrimary24G: (ch == 1 || ch == 6 || ch == 11));
                ratings.Add(rating);
            }

            // Find best recommended channel among primary non-overlapping channels (1, 6, 11)
            var bestPrimary = ratings
                .Where(r => r.Channel == 1 || r.Channel == 6 || r.Channel == 11)
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.CoChannelCount)
                .FirstOrDefault();

            if (bestPrimary != null)
            {
                bestPrimary.IsRecommended = true;
                bestPrimary.StatusText = "Best 2.4 GHz Choice (Recommended)";
                bestPrimary.BadgeBrush = Brushes.LimeGreen;
            }

            return ratings;
        }

        /// <summary>
        /// Analyzes 5 GHz channels and calculates ratings.
        /// </summary>
        public List<ChannelRating> Analyze5GChannels(IEnumerable<WifiNetwork> networks)
        {
            var netList = networks.Where(n => n.Band.Contains("5")).ToList();
            var ratings = new List<ChannelRating>();

            foreach (var ch in Standard5GChannels)
            {
                int coChannel = 0;
                int overlap = 0;
                double penalty = 0;

                foreach (var net in netList)
                {
                    int diff = Math.Abs(net.Channel - ch);
                    double weight = Math.Max(0.1, (net.SignalPercent / 100.0));

                    if (diff == 0)
                    {
                        coChannel++;
                        penalty += 20 * weight;
                    }
                    else if (diff <= 8 && net.ChannelWidthMHz >= 80)
                    {
                        overlap++;
                        penalty += 10 * weight;
                    }
                }

                int score = Math.Max(0, (int)Math.Round(100 - penalty));
                var rating = new ChannelRating
                {
                    Channel = ch,
                    Band = "5 GHz",
                    Score = score,
                    CoChannelCount = coChannel,
                    OverlapCount = overlap
                };

                FormatRating(rating, isPrimary24G: false);
                ratings.Add(rating);
            }

            var best5G = ratings.OrderByDescending(r => r.Score).ThenBy(r => r.CoChannelCount).FirstOrDefault();
            if (best5G != null)
            {
                best5G.IsRecommended = true;
                best5G.StatusText = "Best 5 GHz Choice (Cleanest)";
                best5G.BadgeBrush = Brushes.LimeGreen;
            }

            return ratings;
        }

        /// <summary>
        /// Analyzes 6 GHz (Wi-Fi 6E / Wi-Fi 7) channels and calculates ratings with Preferred Scanning Channel (PSC) prioritization.
        /// </summary>
        public List<ChannelRating> Analyze6GChannels(IEnumerable<WifiNetwork> networks)
        {
            var netList = networks.Where(n => (n.Band != null && n.Band.Contains("6")) || n.FrequencyMHz >= 5925).ToList();
            var ratings = new List<ChannelRating>();

            foreach (var ch in Standard6GChannels)
            {
                int coChannel = 0;
                int overlap = 0;
                double penalty = 0;
                bool isPsc = PreferredScanningChannels6G.Contains(ch);

                foreach (var net in netList)
                {
                    int diff = Math.Abs(net.Channel - ch);
                    double weight = Math.Max(0.1, (net.SignalPercent / 100.0));

                    if (diff == 0)
                    {
                        coChannel++;
                        penalty += 20 * weight;
                    }
                    else if (diff <= 16 && (net.ChannelWidthMHz >= 80 || net.ChannelWidthMHz == 0))
                    {
                        overlap++;
                        penalty += 8 * weight;
                    }
                }

                int score = Math.Max(0, (int)Math.Round(100 - penalty));
                var rating = new ChannelRating
                {
                    Channel = ch,
                    Band = "6 GHz",
                    Score = score,
                    CoChannelCount = coChannel,
                    OverlapCount = overlap
                };

                FormatRating(rating, isPrimary24G: false);
                if (isPsc && rating.Score >= 60)
                {
                    rating.StatusText = rating.Score >= 85 ? "Clear (PSC Fast Beacon)" : "PSC (Low Congestion)";
                }
                ratings.Add(rating);
            }

            // Prioritize Preferred Scanning Channels (PSC) for recommended recommendation
            var best6G = ratings
                .Where(r => PreferredScanningChannels6G.Contains(r.Channel))
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.CoChannelCount)
                .FirstOrDefault() ?? ratings.OrderByDescending(r => r.Score).FirstOrDefault();

            if (best6G != null)
            {
                best6G.IsRecommended = true;
                best6G.StatusText = "Best 6 GHz Choice (Cleanest PSC)";
                best6G.BadgeBrush = Brushes.LimeGreen;
            }

            return ratings;
        }

        private static void FormatRating(ChannelRating rating, bool isPrimary24G)
        {
            if (rating.Score >= 85)
            {
                rating.Stars = "★★★★★";
                rating.StatusText = isPrimary24G ? "Excellent (Non-overlapping)" : "Clear";
                rating.BadgeBrush = Brushes.LimeGreen;
            }
            else if (rating.Score >= 70)
            {
                rating.Stars = "★★★★☆";
                rating.StatusText = "Good (Low Congestion)";
                rating.BadgeBrush = Brushes.Cyan;
            }
            else if (rating.Score >= 50)
            {
                rating.Stars = "★★★☆☆";
                rating.StatusText = "Moderate Traffic";
                rating.BadgeBrush = Brushes.Gold;
            }
            else if (rating.Score >= 30)
            {
                rating.Stars = "★★☆☆☆";
                rating.StatusText = "Congested";
                rating.BadgeBrush = Brushes.Orange;
            }
            else
            {
                rating.Stars = "★☆☆☆☆";
                rating.StatusText = "Heavy Interference";
                rating.BadgeBrush = Brushes.Red;
            }

            // Flag non-standard 2.4 GHz channels if they cause overlap
            if (rating.Band.Contains("2.4") && !isPrimary24G && rating.Score > 50)
            {
                rating.StatusText = "Overlapping Channel (Avoid 2-5, 7-10)";
                rating.BadgeBrush = Brushes.Orange;
            }
        }

        /// <summary>
        /// Cross-analyzes ambient 2.4 GHz, 5 GHz, and 6 GHz spectrum and active client SNR to provide smart band &amp; channel steering advice.
        /// When automated band-steering policy enforcement is enabled, actively steers dual-band clients to 5 GHz / 6 GHz
        /// if client SNR exceeds the configured threshold, with dynamic fallback to 2.4 GHz if clients are distant.
        /// </summary>
        public (string RecommendedBand, string RecommendedChannel, string Advice) GetSmartSteeringRecommendation(
            IEnumerable<WifiNetwork> networks,
            int clientCount = 0,
            double avgClientSnr = 70.0,
            int snrThreshold = 60,
            bool enforcePolicy = true)
        {
            var netList = networks.ToList();
            bool has5GNetworks = netList.Any(n => (n.Band != null && (n.Band.Contains("5") || n.Band.Contains("6"))) || n.Channel > 14);

            var r24 = Analyze24GChannels(netList);
            var r5 = Analyze5GChannels(netList);

            var best24 = r24.Where(r => r.Channel == 1 || r.Channel == 6 || r.Channel == 11)
                            .OrderByDescending(r => r.Score)
                            .FirstOrDefault();

            var best5 = r5.OrderByDescending(r => r.Score).FirstOrDefault();

            int score24 = best24?.Score ?? 50;
            int score5 = has5GNetworks ? (best5?.Score ?? 90) : 0;
            int ch24 = best24?.Channel ?? 1;

            // Automated Band-Steering Policy Enforcement
            if (enforcePolicy && has5GNetworks)
            {
                // Fallback condition: If clients are connected but experience weak SNR (< 45%), steer to 2.4 GHz for range safety
                if (clientCount > 0 && avgClientSnr < 45.0)
                {
                    string fallbackAdvice = $"⚠️ Dynamic Steering Fallback: Connected clients report low SNR ({avgClientSnr:F0}% < 45%). Steered to 2.4 GHz (Channel {ch24}) to maximize coverage and prevent drops.";
                    return ("2.4 GHz", "2.4 GHz Band (Standard Compatibility)", fallbackAdvice);
                }

                // Optimal condition: Client SNR >= threshold (or no clients yet, preparing clean 5G/6G broadcast)
                if (clientCount == 0 || avgClientSnr >= snrThreshold)
                {
                    string policyAdvice = clientCount > 0
                        ? $"⚡ Band-Steering Policy Active: Actively guiding dual-band clients to 5 GHz / 6 GHz (SNR: {avgClientSnr:F0}% ≥ {snrThreshold}% threshold, lowest latency)."
                        : $"⚡ Band-Steering Policy Active: Spectrum primed for 5 GHz / 6 GHz dual-band clients (Score: {score5}/100, zero crosstalk).";
                    return ("5 GHz", "5 GHz Band (High Speed 5 GHz • Auto-channel)", policyAdvice);
                }
            }

            // Standard RF cleanliness comparison fallback
            if (has5GNetworks && (score24 < 55 || score5 >= 80))
            {
                string advice = score24 < 55
                    ? $"⚠️ 2.4 GHz is congested (Score: {score24}/100). Auto-Steer recommends 5 GHz for lowest latency and zero crosstalk."
                    : $"⚡ 5 GHz Band Recommended: Clean frequency spectrum with max bandwidth and minimal latency (Score: {score5}/100).";
                return ("5 GHz", "5 GHz Band (High Speed 5 GHz • Auto-channel)", advice);
            }
            else
            {
                string advice = has5GNetworks
                    ? $"✓ 2.4 GHz Channel {ch24} is optimal (Score: {score24}/100, {best24?.CoChannelCount ?? 0} competing APs)."
                    : $"✓ 2.4 GHz Band Recommended (Channel {ch24}): Optimal standard spectrum ({best24?.CoChannelCount ?? 0} competing APs, score: {score24}/100).";
                return ("2.4 GHz", "2.4 GHz Band (Standard Compatibility)", advice);
            }
        }
    }
}
