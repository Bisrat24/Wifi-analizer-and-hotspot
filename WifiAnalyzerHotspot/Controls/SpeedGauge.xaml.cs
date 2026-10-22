using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WifiAnalyzerHotspot.Controls
{
    public partial class SpeedGauge : UserControl
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(
                nameof(Value),
                typeof(double),
                typeof(SpeedGauge),
                new PropertyMetadata(0.0, OnValueChanged));

        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(
                nameof(MaxValue),
                typeof(double),
                typeof(SpeedGauge),
                new PropertyMetadata(100.0, OnValueChanged));

        public static readonly DependencyProperty StageTextProperty =
            DependencyProperty.Register(
                nameof(StageText),
                typeof(string),
                typeof(SpeedGauge),
                new PropertyMetadata("READY", (d, e) =>
                {
                    if (d is SpeedGauge g && e.NewValue is string s)
                        g.TxtStage.Text = s.ToUpperInvariant();
                }));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double MaxValue
        {
            get => (double)GetValue(MaxValueProperty);
            set => SetValue(MaxValueProperty, value);
        }

        public string StageText
        {
            get => (string)GetValue(StageTextProperty);
            set => SetValue(StageTextProperty, value);
        }

        public SpeedGauge()
        {
            InitializeComponent();
            Loaded += (s, e) => DrawArcs();
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SpeedGauge gauge)
            {
                gauge.UpdateGauge();
            }
        }

        private void UpdateGauge()
        {
            TxtSpeedValue.Text = Value.ToString("F1");

            // Auto-scale MaxValue if speed exceeds current MaxValue
            if (Value > MaxValue)
            {
                MaxValue = Value > 200 ? 500 : 200;
            }

            double percent = Math.Clamp(Value / Math.Max(1.0, MaxValue), 0.0, 1.0);
            double targetAngle = -135 + (percent * 270.0);

            // Animate needle smoothly
            var anim = new DoubleAnimation(targetAngle, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            NeedleTransform.BeginAnimation(RotateTransform.AngleProperty, anim);

            DrawArcs();
        }

        private void DrawArcs()
        {
            const double center = 130;
            const double radius = 100;

            // Background full arc (-135 to +135 deg)
            BackgroundArc.Data = CreateArcGeometry(center, center, radius, -135, 135);

            // Value arc
            double percent = Math.Clamp(Value / Math.Max(1.0, MaxValue), 0.001, 1.0);
            double endAngle = -135 + (percent * 270.0);
            ValueArc.Data = CreateArcGeometry(center, center, radius, -135, endAngle);
        }

        private static PathGeometry CreateArcGeometry(double cx, double cy, double r, double startAngleDeg, double endAngleDeg)
        {
            // Convert to radians (0 is East, 90 is South)
            // In WPF coordinates: offset angle by 90 to match needle (0 is North)
            double startRad = (startAngleDeg - 90) * Math.PI / 180.0;
            double endRad = (endAngleDeg - 90) * Math.PI / 180.0;

            var startPoint = new Point(cx + (r * Math.Cos(startRad)), cy + (r * Math.Sin(startRad)));
            var endPoint = new Point(cx + (r * Math.Cos(endRad)), cy + (r * Math.Sin(endRad)));

            double sweep = endAngleDeg - startAngleDeg;
            bool isLargeArc = sweep > 180.0;

            var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = endPoint,
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = isLargeArc
            });

            var geom = new PathGeometry();
            geom.Figures.Add(figure);
            return geom;
        }
    }
}
