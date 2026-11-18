using System.Windows;
using System.Windows.Controls;

namespace WifiAnalyzerHotspot.Controls
{
    /// <summary>
    /// Two-way bridge between a PasswordBox and a ViewModel string property, so secrets are
    /// never rendered as plain TextBox content.
    /// </summary>
    public static class PasswordBoxHelper
    {
        public static readonly DependencyProperty BoundPasswordProperty =
            DependencyProperty.RegisterAttached("BoundPassword", typeof(string), typeof(PasswordBoxHelper),
                new FrameworkPropertyMetadata(string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

        public static string GetBoundPassword(DependencyObject obj) => (string)obj.GetValue(BoundPasswordProperty);
        public static void SetBoundPassword(DependencyObject obj, string value) => obj.SetValue(BoundPasswordProperty, value);

        public static readonly DependencyProperty UpdateOnPasswordChangedProperty =
            DependencyProperty.RegisterAttached("UpdateOnPasswordChanged", typeof(bool), typeof(PasswordBoxHelper),
                new PropertyMetadata(false, OnUpdateOnPasswordChangedChanged));

        public static bool GetUpdateOnPasswordChanged(DependencyObject obj) => (bool)obj.GetValue(UpdateOnPasswordChangedProperty);
        public static void SetUpdateOnPasswordChanged(DependencyObject obj, bool value) => obj.SetValue(UpdateOnPasswordChangedProperty, value);

        private static bool _updating;

        private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not PasswordBox box || _updating) return;
            string value = e.NewValue as string ?? string.Empty;
            if (box.Password == value) return;
            _updating = true;
            try { box.Password = value; }
            finally { _updating = false; }
        }

        private static void OnUpdateOnPasswordChangedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not PasswordBox box) return;
            box.PasswordChanged -= OnPasswordChanged;
            if ((bool)e.NewValue) box.PasswordChanged += OnPasswordChanged;
        }

        private static void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            var box = (PasswordBox)sender;
            if (_updating) return;
            _updating = true;
            try { SetBoundPassword(box, box.Password); }
            finally { _updating = false; }
        }
    }
}
