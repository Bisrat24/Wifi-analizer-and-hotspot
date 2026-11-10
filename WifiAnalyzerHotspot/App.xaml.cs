using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
        [Flags]
        public enum EXECUTION_STATE : uint
        {
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000,
            ES_SYSTEM_REQUIRED = 0x00000001
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;
        public static readonly int WM_SHOWME = RegisterWindowMessage("WIFI_ANALYZER_AND_HOTSPOT_SHOWME");

        private static Mutex? _singleInstanceMutex;
        public static bool IsStartMinimized { get; private set; } = false;
        public static bool IsAdministrator { get; private set; } = false;
        public static bool IsSessionEnding { get; private set; } = false;

        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WifiAnalyzerHotspot");
        private static readonly string CrashLogPath = Path.Combine(LogDirectory, "crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. Check Administrator elevation
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                IsAdministrator = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                IsAdministrator = false;
            }

            // 2. Command-line argument parsing
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

            // 3. Single instance enforcement
            _singleInstanceMutex = new Mutex(true, "WifiAnalyzerHotspot_SingleInstance_Mutex_Bisrat24", out bool isNewInstance);
            if (!isNewInstance)
            {
                // Another instance is already running; wake it up and bring to front
                PostMessage(HWND_BROADCAST, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // 4. Elevate process priority to High (High responsiveness without starving OS input/audio/DWM)
            try
            {
                using var proc = Process.GetCurrentProcess();
                proc.PriorityClass = ProcessPriorityClass.High;
            }
            catch { }

            // 5. Engage Win32 execution lock on the persistent UI thread so Windows does not sleep or throttle Wi-Fi
            try
            {
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS |
                                        EXECUTION_STATE.ES_SYSTEM_REQUIRED |
                                        EXECUTION_STATE.ES_AWAYMODE_REQUIRED);
            }
            catch { }

            // 6. Run background hotspot priority configuration
            _ = Task.Run(async () =>
            {
                try
                {
                    await new Services.HotspotPriorityService().EngageHighestPriorityAsync();
                }
                catch { }
            });

            // 7. Session ending listener (allows Windows restart/shutdown without blocking)
            SessionEnding += (s, args) =>
            {
                IsSessionEnding = true;
            };

            // 8. Exception handlers
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // Remove persistent NetQoS policies even if the hotspot was left running
                Services.HotspotPriorityService.CleanupAllPolicies();
            }
            catch { }

            try
            {
                // Clear Win32 execution state lock
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            }
            catch { }

            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
            catch { }

            base.OnExit(e);
        }

        private static void SafeLogCrash(string type, string message)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var fi = new FileInfo(CrashLogPath);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    // Keep log bounded to 1MB
                    File.Delete(CrashLogPath);
                }
                File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{type}] {message}\n");
            }
            catch { }
        }

        private static int _dispatcherExCount = 0;
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            int count = Interlocked.Increment(ref _dispatcherExCount);
            if (count <= 10)
            {
                SafeLogCrash("Dispatcher", e.Exception.ToString());
            }
            Debug.WriteLine($"[Handled Dispatcher Exception] {e.Exception.GetType().Name}: {e.Exception.Message}");
            if (count > 25)
            {
                e.Handled = false;
                return;
            }
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                SafeLogCrash("AppDomain", ex.ToString());
                Debug.WriteLine($"[AppDomain Unhandled Exception] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            SafeLogCrash("TaskScheduler", e.Exception.ToString());
            Debug.WriteLine($"[Unobserved Task Exception] {e.Exception.Message}");
            e.SetObserved();
        }
    }
}
