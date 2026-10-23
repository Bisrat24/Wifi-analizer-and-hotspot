using System;
using System.ComponentModel;
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
        private TrayIconManager? _trayManager;
        private bool _isExplicitExit = false;
        private bool _hasShownBalloon = false;

        public MainWindow()
        {
            InitializeComponent();

            Loaded += (s, e) =>
            {
                var wih = new WindowInteropHelper(this);
                var source = HwndSource.FromHwnd(wih.EnsureHandle());
                source?.AddHook(WndProc);

                _trayManager = new TrayIconManager(this);
                _trayManager.Initialize();
                _trayManager.OnOpenRequested += RestoreWindow;
                _trayManager.OnExitRequested += ShutdownApp;
            };

            StateChanged += MainWindow_StateChanged;
            Closing += MainWindow_Closing;
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
            if (!_isExplicitExit)
            {
                e.Cancel = true;
                MinimizeToTray();
            }
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
            _trayManager?.Dispose();
            _trayManager = null;
            base.OnClosed(e);
        }
    }
}