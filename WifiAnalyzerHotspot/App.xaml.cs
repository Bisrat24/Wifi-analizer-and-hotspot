using System;
using System.Diagnostics;
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
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Trap UI dispatcher exceptions so transient UI/binding glitches don't terminate the process
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // Trap AppDomain unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // Trap unobserved background task exceptions
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
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
