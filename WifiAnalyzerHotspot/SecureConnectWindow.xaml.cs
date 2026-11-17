using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WifiAnalyzerHotspot
{
    /// <summary>
    /// Interaction logic for SecureConnectWindow.xaml (AnyConnect-style compact floating window)
    /// </summary>
    public partial class SecureConnectWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        public SecureConnectWindow()
        {
            InitializeComponent();

            Loaded += (s, e) =>
            {
                ApplyDarkThemeToTitleBar();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyDarkThemeToTitleBar();
        }

        private void ApplyDarkThemeToTitleBar()
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                var hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkMode = 1; // 1 = True (enable dark title bar)
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                if (hr != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                }

                // Match app display background #0C0E14: COLORREF 0x00BBGGRR -> R=0x0C, G=0x0E, B=0x14 -> 0x00140E0C
                int captionColor = 0x00140E0C;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

                // Title bar foreground #E2E8F0: COLORREF 0x00BBGGRR -> R=0xE2, G=0xE8, B=0xF0 -> 0x00F0E8E2
                int textColor = 0x00F0E8E2;
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));

                // Window border #1F2538: COLORREF 0x00BBGGRR -> R=0x1F, G=0x25, B=0x38 -> 0x0038251F
                int borderColor = 0x0038251F;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
            catch
            {
                // Silently fallback if dwmapi is not supported
            }
        }
    }
}
