using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WifiAnalyzerHotspot.Converters
{
    public class TabVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Visibility.Collapsed;
            return value.ToString()!.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class TabActiveBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x33));
            bool isActive = value.ToString()!.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase);
            return isActive
                ? new SolidColorBrush(Color.FromArgb(50, 0, 229, 255))
                : new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x22));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class TabActiveBorderBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Brushes.Transparent;
            bool isActive = value.ToString()!.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase);
            return isActive
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF))
                : Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SignalToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int signal)
            {
                if (signal >= 75) return new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76)); // Green
                if (signal >= 50) return new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)); // Cyan
                if (signal >= 30) return new SolidColorBrush(Color.FromRgb(0xFF, 0xEA, 0x00)); // Yellow
                return new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52)); // Red
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BandActiveBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x33));
            bool isSelected = value.ToString()!.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase);
            return isSelected
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x33));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BandActiveForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Brushes.White;
            bool isSelected = value.ToString()!.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase);
            return isSelected
                ? new SolidColorBrush(Color.FromRgb(0x0C, 0x0E, 0x14))
                : Brushes.White;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class HotspotStateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Models.HotspotState state)
            {
                return state switch
                {
                    Models.HotspotState.On => new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76)),
                    Models.HotspotState.InTransition => new SolidColorBrush(Color.FromRgb(0xFF, 0xEA, 0x00)),
                    _ => new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52))
                };
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool flag && flag;
            if (parameter?.ToString() == "Inverse") b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return false;
        }
    }
}
