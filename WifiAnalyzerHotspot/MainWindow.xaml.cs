using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WifiAnalyzerHotspot.Services;

namespace WifiAnalyzerHotspot
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        private TrayIconManager? _trayManager;
        private bool _isExplicitExit = false;
        private bool _hasShownBalloon = false;

        public MainWindow()
        {
            App.LogStartupMilestone("MainWindow ctor enter");
            InitializeComponent();
            App.LogStartupMilestone("MainWindow InitializeComponent complete");

            Loaded += (s, e) =>
            {
                App.LogStartupMilestone("MainWindow.Loaded event fired");
                var wih = new WindowInteropHelper(this);
                var hwnd = wih.EnsureHandle();
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);

                ApplyDarkThemeToTitleBar();

                // Dynamic screen size responsiveness: ensure window fits within the display's usable work area
                try
                {
                    var workArea = SystemParameters.WorkArea;
                    if (workArea.Width > 0 && workArea.Height > 0)
                    {
                        if (ActualHeight > workArea.Height || Height > workArea.Height)
                        {
                            Height = Math.Max(MinHeight, workArea.Height - 30);
                            Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
                        }
                        if (ActualWidth > workArea.Width || Width > workArea.Width)
                        {
                            Width = Math.Max(MinWidth, workArea.Width - 30);
                            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
                        }
                    }
                }
                catch
                {
                    // Fall back to XAML design defaults if work area inspection fails
                }

                _trayManager = new TrayIconManager(this);
                _trayManager.Initialize();
                _trayManager.OnOpenRequested += RestoreWindow;
                _trayManager.OnExitRequested += ShutdownApp;

                if (App.IsStartMinimized)
                {
                    Dispatcher.InvokeAsync(MinimizeToTray, System.Windows.Threading.DispatcherPriority.Background);
                }
            };

            ContentRendered += (s, e) =>
            {
                App.LogStartupMilestone("MainWindow.ContentRendered (First Frame Paint)");
                App.FlushStartupMilestones();
            };

            StateChanged += MainWindow_StateChanged;
            Closing += MainWindow_Closing;
            App.LogStartupMilestone("MainWindow ctor exit");
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
                // Silently ignore if dwmapi features are unsupported on host OS
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == App.WM_SHOWME)
            {
                RestoreWindow();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                MinimizeToTray();
            }
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // Allow clean Windows shutdown / restart without blocking the OS
            if (_isExplicitExit || App.IsSessionEnding)
            {
                e.Cancel = false;
                return;
            }

            e.Cancel = true;
            MinimizeToTray();
        }

        private void MinimizeToTray()
        {
            Hide();
            if (!_hasShownBalloon && _trayManager != null)
            {
                _hasShownBalloon = true;
                _trayManager.ShowNotification(
                    "Wi-Fi Analyzer & Hotspot Pro",
                    "Application is minimized to the system tray. Click the tray icon to restore.");
            }
        }

        private void RestoreWindow()
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            Focus();
        }

        private void ShutdownApp()
        {
            _isExplicitExit = true;
            _trayManager?.Dispose();
            _trayManager = null;
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
            _trayManager?.Dispose();
            _trayManager = null;
            base.OnClosed(e);
        }
    }
}
