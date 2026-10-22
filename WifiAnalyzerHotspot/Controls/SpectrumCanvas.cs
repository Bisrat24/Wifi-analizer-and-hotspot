using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Controls
{
    public class SpectrumCanvas : FrameworkElement
    {
        public static readonly DependencyProperty NetworksProperty =
            DependencyProperty.Register(
                nameof(Networks),
                typeof(IEnumerable<WifiNetwork>),
                typeof(SpectrumCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnNetworksPropertyChanged));

        public static readonly DependencyProperty SelectedBandProperty =
            DependencyProperty.Register(
                nameof(SelectedBand),
                typeof(string),
                typeof(SpectrumCanvas),
                new FrameworkPropertyMetadata("2.4 GHz", FrameworkPropertyMetadataOptions.AffectsRender));

        private static void OnNetworksPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SpectrumCanvas canvas)
            {
                if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged oldCol)
                {
                    oldCol.CollectionChanged -= canvas.OnCollectionChanged;
                }
                if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged newCol)
                {
                    newCol.CollectionChanged += canvas.OnCollectionChanged;
                }
                canvas.InvalidateVisual();
            }
        }

        private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Render);
        }

        public SpectrumCanvas()
        {
            Loaded += (s, e) => InvalidateVisual();
            Unloaded += (s, e) =>
            {
                if (Networks is System.Collections.Specialized.INotifyCollectionChanged col)
                {
                    col.CollectionChanged -= OnCollectionChanged;
                }
            };
        }

        public IEnumerable<WifiNetwork>? Networks
        {
            get => (IEnumerable<WifiNetwork>?)GetValue(NetworksProperty);
            set => SetValue(NetworksProperty, value);
        }

        public string SelectedBand
        {
            get => (string)GetValue(SelectedBandProperty);
            set => SetValue(SelectedBandProperty, value);
        }

        private static readonly Typeface FontNormal = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface FontBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private static readonly SolidColorBrush BgBrush;
        private static readonly SolidColorBrush TextBrush;
        private static readonly SolidColorBrush PrimaryChBrush;
        private static readonly SolidColorBrush TitleBrush;
        private static readonly SolidColorBrush SubtitleBrush;
        private static readonly Pen GridPen;
        private static readonly Pen PrimaryPen;
        private static readonly Pen AxisPen;

        static SpectrumCanvas()
        {
            BgBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1E));
            BgBrush.Freeze();

            TextBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x96, 0xAB));
            TextBrush.Freeze();

            PrimaryChBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
            PrimaryChBrush.Freeze();

            TitleBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
            TitleBrush.Freeze();

            SubtitleBrush = new SolidColorBrush(Color.FromRgb(0x71, 0x7E, 0x99));
            SubtitleBrush.Freeze();

            GridPen = new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1);
            GridPen.Freeze();

            PrimaryPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 0, 229, 255)), 1.5);
            PrimaryPen.Freeze();

            AxisPen = new Pen(new SolidColorBrush(Color.FromRgb(0x32, 0x3A, 0x4E)), 1.5);
            AxisPen.Freeze();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth;
            double h = ActualHeight;

            if (w < 120 || h < 120) return;

            // Margins
            double leftMargin = 55;
            double rightMargin = 25;
            double topMargin = 40;
            double bottomMargin = 45;

            double graphW = w - leftMargin - rightMargin;
            double graphH = h - topMargin - bottomMargin;

            // Draw Dark Metallic Canvas Background
            dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, w, h), 8, 8);

            // Draw Y-Axis (Signal dBm: -100 to -20 dBm)
            const double minDbm = -100;
            const double maxDbm = -20;

            for (double dbm = minDbm; dbm <= maxDbm; dbm += 10)
            {
                double ratio = (dbm - minDbm) / (maxDbm - minDbm);
                double y = topMargin + graphH - (ratio * graphH);

                dc.DrawLine(GridPen, new Point(leftMargin, y), new Point(w - rightMargin, y));

                var label = new FormattedText(
                    $"{dbm:F0} dBm",
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    FontNormal,
                    11,
                    TextBrush,
                    1.25);

                dc.DrawText(label, new Point(leftMargin - label.Width - 8, y - (label.Height / 2)));
            }

            bool is24G = !(SelectedBand ?? "").Contains("5");

            // Setup X-Axis Channels
            // Extra margin on minCh/maxCh ensures edge channels (CH 1, CH 14, CH 36, CH 165)
            // have enough room so their ±2 channel skirts don't extend off-canvas.
            List<int> channelList = is24G
                ? new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 }
                : new List<int> { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 };

            // Extend virtual channel range by 2.5 ch units on each side so that the skirt of
            // CH 1 (leftmost) and CH 14/165 (rightmost) always fits inside the drawn graph area.
            double minCh = is24G ? -1.5 : 29.5;
            double maxCh = is24G ? 16.5 : 172.0;

            // Helper: raw channel position (may be outside graph for edge channels — see clamp below)
            Func<double, double> channelToX = ch =>
                leftMargin + ((ch - minCh) / (maxCh - minCh) * graphW);

            // Graph boundary for clamping curve endpoints
            double graphLeft  = leftMargin;
            double graphRight = w - rightMargin;

            // Draw X-Axis channel gridlines and tick labels
            foreach (var ch in channelList)
            {
                double x = channelToX(ch);
                bool isPrimary = is24G && (ch == 1 || ch == 6 || ch == 11);

                dc.DrawLine(isPrimary ? PrimaryPen : GridPen, new Point(x, topMargin), new Point(x, topMargin + graphH));

                var chBrush = isPrimary ? PrimaryChBrush : TextBrush;

                var chText = new FormattedText(
                    is24G ? $"CH {ch}" : $"{ch}",
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    isPrimary ? FontBold : FontNormal,
                    is24G ? 11 : 10,
                    chBrush,
                    1.25);

                dc.DrawText(chText, new Point(x - (chText.Width / 2), topMargin + graphH + 8));
            }

            // Draw X-Axis baseline
            dc.DrawLine(AxisPen, new Point(leftMargin, topMargin + graphH), new Point(w - rightMargin, topMargin + graphH));

            // Filter visible networks (matching band or channel number)
            var visibleNetworks = (Networks ?? Enumerable.Empty<WifiNetwork>())
                .Where(n => is24G ? (n.Band.Contains("2.4") || (n.Channel >= 1 && n.Channel <= 14))
                                  : (n.Band.Contains("5") || n.Channel > 14))
                .OrderBy(n => n.Rssi) // Draw weaker networks first, so stronger appear in front
                .ToList();

            double baseY = topMargin + graphH;

            // 1. FIRST PASS: Draw OFDM spectral curve geometries and peak dots
            foreach (var net in visibleNetworks)
            {
                double rssiClamped = Math.Clamp((double)net.Rssi, minDbm, maxDbm);
                double peakRatio = (rssiClamped - minDbm) / (maxDbm - minDbm);
                double peakY = baseY - (peakRatio * graphH);

                double centerX = channelToX(net.Channel);

                // Channel width span in channel units
                double chWidthSpan = is24G
                    ? (net.ChannelWidthMHz >= 40 ? 8.0 : 4.0)
                    : (net.ChannelWidthMHz >= 160 ? 32.0 : net.ChannelWidthMHz >= 80 ? 16.0 : net.ChannelWidthMHz >= 40 ? 8.0 : 4.0);

                // Raw (unclamped) curve extents
                double rawLeftX  = channelToX(net.Channel - (chWidthSpan / 2.0));
                double rawRightX = channelToX(net.Channel + (chWidthSpan / 2.0));

                // Clamp to the visible graph area so no geometry bleeds beyond the axis borders
                double leftX  = Math.Max(rawLeftX,  graphLeft);
                double rightX = Math.Min(rawRightX, graphRight);

                if (rightX <= leftX) continue;

                double totalW = rightX - leftX;

                var color = net.ColorBrush.Color;
                var strokePen = new Pen(net.ColorBrush, net.IsConnected ? 2.4 : 1.8);
                strokePen.Freeze();

                var fillBrush = new LinearGradientBrush(
                    Color.FromArgb((byte)(net.IsConnected ? 85 : 48), color.R, color.G, color.B),
                    Color.FromArgb(3, color.R, color.G, color.B),
                    new Point(0, 0),
                    new Point(0, 1));
                fillBrush.Freeze();

                // Standard WiFi Analyzer parabolic dome formula:
                // y = peakY + ((x - centerX) / halfWidth)^2 * (baseY - peakY)
                // This guarantees the absolute peak is exactly at centerX, peakY, with smooth symmetric parabolic falloff to baseY
                double halfW = Math.Max(1.0, (rawRightX - rawLeftX) / 2.0);
                double height = baseY - peakY;

                const int steps = 32;
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    ctx.BeginFigure(new Point(leftX, baseY), isFilled: true, isClosed: true);

                    for (int s = 0; s <= steps; s++)
                    {
                        double curX = leftX + ((double)s / steps * totalW);
                        double normX = (curX - centerX) / halfW;
                        double curY = Math.Min(baseY, peakY + (normX * normX * height));
                        ctx.LineTo(new Point(curX, curY), isStroked: true, isSmoothJoin: true);
                    }

                    ctx.LineTo(new Point(rightX, baseY), isStroked: true, isSmoothJoin: false);
                }
                geom.Freeze();

                // Clip to graph area
                dc.PushClip(new RectangleGeometry(new Rect(graphLeft, topMargin, graphRight - graphLeft, graphH + 2)));
                dc.DrawGeometry(fillBrush, strokePen, geom);
                dc.Pop();

                // Apex dot at exact peak
                if (centerX >= graphLeft && centerX <= graphRight)
                {
                    var dotBrush = new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B));
                    dotBrush.Freeze();
                    dc.DrawEllipse(dotBrush, null, new Point(centerX, peakY), 3.5, 3.5);
                }
            }

            // 2. SECOND PASS: Collision-avoidance badge labels with leader lines
            // Sort so connected networks and strongest networks get primary ideal badge positions
            var networksForLabels = visibleNetworks
                .OrderByDescending(n => n.IsConnected)
                .ThenByDescending(n => n.Rssi)
                .ToList();

            var placedBadgeRects = new List<Rect>();

            foreach (var net in networksForLabels)
            {
                double rssiClamped = Math.Clamp((double)net.Rssi, minDbm, maxDbm);
                double peakRatio = (rssiClamped - minDbm) / (maxDbm - minDbm);
                double peakY = baseY - (peakRatio * graphH);
                double centerX = channelToX(net.Channel);

                string labelText = $"{net.DisplayName} ({net.Rssi} dBm)";
                if (net.IsConnected) labelText = "★ " + labelText;

                var color = net.ColorBrush.Color;
                var textBrush = net.IsConnected
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF))
                    : new SolidColorBrush(Color.FromRgb(
                        (byte)Math.Min(255, color.R + 60),
                        (byte)Math.Min(255, color.G + 60),
                        (byte)Math.Min(255, color.B + 60)));
                textBrush.Freeze();

                var tagText = new FormattedText(
                    labelText,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    FontBold,
                    10.5,
                    textBrush,
                    1.25);

                double badgeW = tagText.Width + 14;
                double badgeH = tagText.Height + 6;

                // Candidate search for collision avoidance
                double idealX = Math.Clamp(centerX - (badgeW / 2.0), leftMargin + 2, w - rightMargin - badgeW - 2);
                double idealY = peakY - badgeH - 6;

                // Generate candidate slots (stagger upward, then sideways)
                var candidateSlots = new List<Rect>();

                // Slot 0: Ideal direct peak position
                candidateSlots.Add(new Rect(idealX, idealY, badgeW, badgeH));

                // Slots 1-3: Stagger upward
                for (int tier = 1; tier <= 4; tier++)
                {
                    double testY = idealY - (tier * (badgeH + 4));
                    if (testY >= topMargin - 15)
                    {
                        candidateSlots.Add(new Rect(idealX, testY, badgeW, badgeH));
                    }
                }

                // Slots 4-7: Stagger horizontally left/right with offset
                double offsetStep = badgeW * 0.65;
                double leftCandidateX = Math.Clamp(idealX - offsetStep, leftMargin + 2, w - rightMargin - badgeW - 2);
                double rightCandidateX = Math.Clamp(idealX + offsetStep, leftMargin + 2, w - rightMargin - badgeW - 2);

                candidateSlots.Add(new Rect(leftCandidateX, idealY, badgeW, badgeH));
                candidateSlots.Add(new Rect(rightCandidateX, idealY, badgeW, badgeH));

                for (int tier = 1; tier <= 3; tier++)
                {
                    double testY = idealY - (tier * (badgeH + 4));
                    if (testY >= topMargin - 15)
                    {
                        candidateSlots.Add(new Rect(leftCandidateX, testY, badgeW, badgeH));
                        candidateSlots.Add(new Rect(rightCandidateX, testY, badgeW, badgeH));
                    }
                }

                // Select the first candidate slot that doesn't intersect existing placed badges
                Rect chosenRect = candidateSlots[0];
                bool foundClean = false;

                foreach (var candidate in candidateSlots)
                {
                    // Add 2px margin padding test
                    var paddedTest = new Rect(candidate.X - 2, candidate.Y - 2, candidate.Width + 4, candidate.Height + 4);
                    bool overlaps = false;
                    foreach (var existing in placedBadgeRects)
                    {
                        if (paddedTest.IntersectsWith(existing))
                        {
                            overlaps = true;
                            break;
                        }
                    }

                    if (!overlaps)
                    {
                        chosenRect = candidate;
                        foundClean = true;
                        break;
                    }
                }

                // If all candidates overlap (extremely crowded channel), offset by small index delta
                if (!foundClean)
                {
                    int placedCountOnSameCh = placedBadgeRects.Count(r => Math.Abs(r.X + (r.Width / 2) - centerX) < 30);
                    double forcedY = idealY - (placedCountOnSameCh * (badgeH + 4));
                    chosenRect = new Rect(idealX, forcedY, badgeW, badgeH);
                }

                placedBadgeRects.Add(chosenRect);

                // If badge is displaced from the apex, draw a sleek leader connector line
                double badgeCenterX = chosenRect.X + (chosenRect.Width / 2.0);
                double badgeBottomY = chosenRect.Y + chosenRect.Height;

                if (Math.Abs(badgeCenterX - centerX) > 8 || Math.Abs(badgeBottomY - peakY) > 12)
                {
                    var leaderPen = new Pen(new SolidColorBrush(Color.FromArgb(140, color.R, color.G, color.B)), 1.0);
                    leaderPen.DashStyle = DashStyles.Dot;
                    leaderPen.Freeze();

                    Point lineStart = new Point(centerX, peakY - 3);
                    Point lineEnd = new Point(badgeCenterX, badgeBottomY);
                    dc.DrawLine(leaderPen, lineStart, lineEnd);
                }

                // Draw badge background pill
                var badgeBg = new SolidColorBrush(Color.FromArgb(225, 0x0E, 0x12, 0x1D));
                badgeBg.Freeze();

                var badgeBorderPen = new Pen(
                    new SolidColorBrush(Color.FromArgb((byte)(net.IsConnected ? 220 : 130), color.R, color.G, color.B)),
                    net.IsConnected ? 1.5 : 1.0);
                badgeBorderPen.Freeze();

                dc.DrawRoundedRectangle(badgeBg, badgeBorderPen, chosenRect, 4, 4);

                // Draw text inside badge
                dc.DrawText(tagText, new Point(chosenRect.X + 7, chosenRect.Y + 3));
            }

            // Legend / Top Band Title & Count
            string bandTitle = is24G ? "2.4 GHz Band (Channels 1 - 14)" : "5 GHz Band (Channels 36 - 165)";
            var titleText = new FormattedText(
                bandTitle,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                FontBold,
                13,
                TitleBrush,
                1.25);
            dc.DrawText(titleText, new Point(leftMargin, 11));

            string subCount = visibleNetworks.Count > 0
                ? $"{visibleNetworks.Count} active network{(visibleNetworks.Count == 1 ? "" : "s")} detected"
                : "Scanning for networks...";
            var subText = new FormattedText(
                subCount,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                FontNormal,
                11,
                SubtitleBrush,
                1.25);
            dc.DrawText(subText, new Point(leftMargin + titleText.Width + 14, 13));
        }
    }
}
