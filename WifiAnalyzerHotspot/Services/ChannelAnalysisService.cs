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
        /// Cross-analyzes ambient 2.4 GHz and 5 GHz spectrum to provide smart band & channel steering advice.
        /// </summary>
        public (string RecommendedBand, string RecommendedChannel, string Advice) GetSmartSteeringRecommendation(IEnumerable<WifiNetwork> networks)
        {
            var netList = networks.ToList();
            bool has5GNetworks = netList.Any(n => (n.Band != null && n.Band.Contains("5")) || n.Channel > 14);

            var r24 = Analyze24GChannels(netList);
            var r5 = Analyze5GChannels(netList);

            var best24 = r24.Where(r => r.Channel == 1 || r.Channel == 6 || r.Channel == 11)
                            .OrderByDescending(r => r.Score)
                            .FirstOrDefault();

            var best5 = r5.OrderByDescending(r => r.Score).FirstOrDefault();

            int score24 = best24?.Score ?? 50;
            int score5 = has5GNetworks ? (best5?.Score ?? 90) : 0;

            if (has5GNetworks && (score24 < 55 || score5 >= 80))
            {
                string advice = score24 < 55
                    ? $"⚠️ 2.4 GHz is congested (Score: {score24}/100). Auto-Steer recommends 5 GHz for lowest latency and zero crosstalk."
                    : $"⚡ 5 GHz Band Recommended: Clean frequency spectrum with max bandwidth and minimal latency (Score: {score5}/100).";
                return ("5 GHz", "5 GHz Band (High Speed 5 GHz • Auto-channel)", advice);
            }
            else
            {
                int ch = best24?.Channel ?? 1;
                string advice = has5GNetworks
                    ? $"✓ 2.4 GHz Channel {ch} is optimal (Score: {score24}/100, {best24?.CoChannelCount ?? 0} competing APs)."
                    : $"✓ 2.4 GHz Band Recommended (Channel {ch}): Optimal standard spectrum ({best24?.CoChannelCount ?? 0} competing APs, score: {score24}/100).";
                return ("2.4 GHz", "2.4 GHz Band (Standard Compatibility)", advice);
            }
        }
    }
}
