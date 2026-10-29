using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Controls
{
    public class RssiTimelineCanvas : FrameworkElement
    {
        public static readonly DependencyProperty HistoryProperty =
            DependencyProperty.Register(
                nameof(History),
                typeof(IEnumerable<RssiHistoryPoint>),
                typeof(RssiTimelineCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnHistoryPropertyChanged));

        public static readonly DependencyProperty ConnectedSsidProperty =
            DependencyProperty.Register(
                nameof(ConnectedSsid),
                typeof(string),
                typeof(RssiTimelineCanvas),
                new FrameworkPropertyMetadata("Connected Wi-Fi", FrameworkPropertyMetadataOptions.AffectsRender));

        private static void OnHistoryPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RssiTimelineCanvas canvas)
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

        public IEnumerable<RssiHistoryPoint>? History
        {
            get => (IEnumerable<RssiHistoryPoint>?)GetValue(HistoryProperty);
            set => SetValue(HistoryProperty, value);
        }

        public string ConnectedSsid
        {
            get => (string)GetValue(ConnectedSsidProperty);
            set => SetValue(ConnectedSsidProperty, value);
        }

        private static readonly Typeface FontNormal = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface FontBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private static readonly SolidColorBrush BgBrush = new(Color.FromRgb(0x10, 0x13, 0x1E));
        private static readonly SolidColorBrush TextDimBrush = new(Color.FromRgb(0x78, 0x86, 0xA0));
        private static readonly SolidColorBrush CyanBrush = new(Color.FromRgb(0x00, 0xE5, 0xFF));
        private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x00, 0xE6, 0x76));
        private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(0xFF, 0xEA, 0x00));
        private static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(0xFF, 0x91, 0x00));
        private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xFF, 0x52, 0x52));

        private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(35, 0xFF, 0xFF, 0xFF)), 1.0);
        private static readonly Pen AxisPen = new(new SolidColorBrush(Color.FromRgb(0x28, 0x30, 0x48)), 1.5);
        private static readonly Pen LinePen = new(new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), 2.5);

        static RssiTimelineCanvas()
        {
            BgBrush.Freeze();
            TextDimBrush.Freeze();
            CyanBrush.Freeze();
            GreenBrush.Freeze();
            YellowBrush.Freeze();
            OrangeBrush.Freeze();
            RedBrush.Freeze();
            GridPen.Freeze();
            AxisPen.Freeze();
            LinePen.Freeze();
        }

        public RssiTimelineCanvas()
        {
            Loaded += (s, e) => InvalidateVisual();
            Unloaded += (s, e) =>
            {
                if (History is System.Collections.Specialized.INotifyCollectionChanged col)
                {
                    col.CollectionChanged -= OnCollectionChanged;
                }
            };
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 100 || h < 100) return;

            // Draw Background
            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));

            double left = 60;
            double right = w - 24;
            double top = 38;
            double bottom = h - 30;
            double plotW = right - left;
            double plotH = bottom - top;

            if (plotW <= 0 || plotH <= 0) return;

            // dBm Range: -30 (top) down to -95 (bottom)
            const double maxDbm = -30.0;
            const double minDbm = -95.0;
            const double dbmSpan = maxDbm - minDbm; // 65 dBm

            double DbmToY(double dbm)
            {
                double clamped = Math.Clamp(dbm, minDbm, maxDbm);
                double ratio = (maxDbm - clamped) / dbmSpan;
                return top + ratio * plotH;
            }

            // Draw Horizontal Guidelines and Labels
            (double dbm, string label, Brush brush)[] levels = new (double dbm, string label, Brush brush)[]
            {
                (-30.0, "-30 dBm • Excellent", GreenBrush),
                (-50.0, "-50 dBm • Good", CyanBrush),
                (-67.0, "-67 dBm • Fair (VoIP / Stream)", YellowBrush),
                (-80.0, "-80 dBm • Weak", OrangeBrush),
                (-90.0, "-90 dBm • Dropping", RedBrush)
            };

            foreach (var (dbm, label, brush) in levels)
            {
                double y = DbmToY(dbm);
                dc.DrawLine(GridPen, new Point(left, y), new Point(right, y));

                var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    FontNormal, 9.5, brush, 1.0);
                dc.DrawText(ft, new Point(left - ft.Width - 6, y - ft.Height / 2));
            }

            // Draw Time Gridlines (-60s, -45s, -30s, -15s, Now)
            const double windowSeconds = 60.0;
            DateTime now = DateTime.Now;

            int[] timeMarks = new[] { 60, 45, 30, 15, 0 };
            foreach (int secAgo in timeMarks)
            {
                double ratio = 1.0 - ((double)secAgo / windowSeconds);
                double x = left + ratio * plotW;

                dc.DrawLine(GridPen, new Point(x, top), new Point(x, bottom));

                string label = secAgo == 0 ? "Now" : $"-{secAgo}s";
                var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    FontNormal, 10, secAgo == 0 ? CyanBrush : TextDimBrush, 1.0);
                dc.DrawText(ft, new Point(x - ft.Width / 2, bottom + 6));
            }

            // Draw Axes Border
            dc.DrawLine(AxisPen, new Point(left, top), new Point(left, bottom));
            dc.DrawLine(AxisPen, new Point(left, bottom), new Point(right, bottom));

            // Extract points within window
            var points = (History ?? Enumerable.Empty<RssiHistoryPoint>())
                .Where(p => (now - p.Timestamp).TotalSeconds <= windowSeconds)
                .OrderBy(p => p.Timestamp)
                .ToList();

            // Header Legend & Stats
            string ssid = !string.IsNullOrWhiteSpace(ConnectedSsid) ? ConnectedSsid : "Active Wi-Fi";
            var headerFt = new FormattedText($"📈 LIVE RSSI TRACKER  •  {ssid}",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, FontBold, 11, CyanBrush, 1.0);
            dc.DrawText(headerFt, new Point(left, 10));

            if (points.Count > 0)
            {
                int curDbm = points.Last().RssiDbm;
                int minVal = points.Min(p => p.RssiDbm);
                int maxVal = points.Max(p => p.RssiDbm);
                int jitter = (maxVal - minVal) / 2;

                string statsText = $"Current: {curDbm} dBm  |  Min: {minVal} dBm  |  Max: {maxVal} dBm  |  Jitter: ±{jitter} dBm";
                var statsFt = new FormattedText(statsText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    FontNormal, 10.5, TextDimBrush, 1.0);
                dc.DrawText(statsFt, new Point(right - statsFt.Width, 11));

                // Plot Polyline & Area Fill
                var screenPoints = new List<Point>();
                foreach (var pt in points)
                {
                    double secAgo = (now - pt.Timestamp).TotalSeconds;
                    double ratio = 1.0 - Math.Clamp(secAgo / windowSeconds, 0.0, 1.0);
                    double x = left + ratio * plotW;
                    double y = DbmToY(pt.RssiDbm);
                    screenPoints.Add(new Point(x, y));
                }

                if (screenPoints.Count >= 2)
                {
                    // Gradient Fill under curve
                    var areaGeo = new StreamGeometry();
                    using (var ctx = areaGeo.Open())
                    {
                        ctx.BeginFigure(new Point(screenPoints[0].X, bottom), true, true);
                        foreach (var pt in screenPoints)
                        {
                            ctx.LineTo(pt, true, false);
                        }
                        ctx.LineTo(new Point(screenPoints.Last().X, bottom), true, false);
                    }
                    areaGeo.Freeze();

                    var fillBrush = new LinearGradientBrush(
                        Color.FromArgb(70, 0x00, 0xE5, 0xFF),
                        Color.FromArgb(5, 0x00, 0xE5, 0xFF),
                        new Point(0, 0), new Point(0, 1));
                    dc.DrawGeometry(fillBrush, null, areaGeo);

                    // Polyline stroke
                    var lineGeo = new StreamGeometry();
                    using (var ctx = lineGeo.Open())
                    {
                        ctx.BeginFigure(screenPoints[0], false, false);
                        for (int i = 1; i < screenPoints.Count; i++)
                        {
                            ctx.LineTo(screenPoints[i], true, false);
                        }
                    }
                    lineGeo.Freeze();
                    dc.DrawGeometry(null, LinePen, lineGeo);

                    // Pulsing latest point dot
                    var lastPt = screenPoints.Last();
                    dc.DrawEllipse(CyanBrush, null, lastPt, 5, 5);
                    dc.DrawEllipse(null, new Pen(CyanBrush, 1.5), lastPt, 8, 8);

                    // Tooltip badge on latest point
                    string curLabel = $"{curDbm} dBm";
                    var curFt = new FormattedText(curLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        FontBold, 9.5, Brushes.Black, 1.0);

                    double badgeW = curFt.Width + 10;
                    double badgeH = curFt.Height + 4;
                    double badgeX = Math.Min(right - badgeW, lastPt.X - badgeW / 2);
                    double badgeY = Math.Max(top + 2, lastPt.Y - badgeH - 8);

                    dc.DrawRoundedRectangle(CyanBrush, null, new Rect(badgeX, badgeY, badgeW, badgeH), 4, 4);
                    dc.DrawText(curFt, new Point(badgeX + 5, badgeY + 2));
                }
            }
            else
            {
                var emptyFt = new FormattedText("Sampling real-time Wi-Fi signals... Walk around to map signal stability.",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, FontNormal, 12, TextDimBrush, 1.0);
                dc.DrawText(emptyFt, new Point(left + (plotW - emptyFt.Width) / 2, top + (plotH - emptyFt.Height) / 2));
            }
        }
    }
}
