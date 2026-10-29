using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WifiAnalyzerHotspot
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;
        public static readonly int WM_SHOWME = RegisterWindowMessage("WIFI_ANALYZER_AND_HOTSPOT_SHOWME");

        private static Mutex? _singleInstanceMutex;
        public static bool IsStartMinimized { get; private set; } = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args != null)
            {
                foreach (var arg in e.Args)
                {
                    if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                    {
                        IsStartMinimized = true;
                        break;
                    }
                }
            }

            _singleInstanceMutex = new Mutex(true, "WifiAnalyzerHotspot_SingleInstance_Mutex_Bisrat24", out bool isNewInstance);
            if (!isNewInstance)
            {
                // Another instance is already running; wake it up and bring to front
                PostMessage(HWND_BROADCAST, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Trap UI dispatcher exceptions so transient UI/binding glitches don't terminate the process
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // Trap AppDomain unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // Trap unobserved background task exceptions
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
            catch { }
            base.OnExit(e);
        }

        private static int _dispatcherExCount = 0;
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            int count = System.Threading.Interlocked.Increment(ref _dispatcherExCount);
            if (count <= 10)
            {
                try
                {
                    System.IO.File.AppendAllText("crash.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Dispatcher] {e.Exception}\n");
                }
                catch { }
            }
            Debug.WriteLine($"[Handled Dispatcher Exception] {e.Exception.GetType().Name}: {e.Exception.Message}");
            Debug.WriteLine(e.Exception.StackTrace);
            if (count > 25)
            {
                e.Handled = false;
                return;
            }
            // Mark handled to prevent crash
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                try
                {
                    System.IO.File.AppendAllText("crash.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [AppDomain] {ex}\n");
                }
                catch { }
                Debug.WriteLine($"[AppDomain Unhandled Exception] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                System.IO.File.AppendAllText("crash.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [TaskScheduler] {e.Exception}\n");
            }
            catch { }
            Debug.WriteLine($"[Unobserved Task Exception] {e.Exception.Message}");
            e.SetObserved();
        }
    }
}
