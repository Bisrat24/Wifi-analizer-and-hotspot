using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Single place for starting child processes: ArgumentList (no string concatenation),
    /// concurrent async stream reads, bounded timeouts and kill/reap on timeout.
    /// </summary>
    internal static class ProcessRunner
    {
        internal sealed record Result(bool Completed, int ExitCode, string Output, string Error);

        internal static async Task<Result> RunAsync(
            string executable,
            IEnumerable<string> args,
            string? stdin = null,
            int timeoutMs = 10000,
            CancellationToken token = default)
        {
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = stdin != null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                foreach (string arg in args) psi.ArgumentList.Add(arg);

                process = Process.Start(psi);
                if (process == null) return new Result(false, -1, string.Empty, "Process did not start.");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(timeoutMs);

                if (stdin != null)
                {
                    await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    process.StandardInput.Close();
                }

                var outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var errTask = process.StandardError.ReadToEndAsync(cts.Token);

                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    string output = await outTask.ConfigureAwait(false);
                    string error = await errTask.ConfigureAwait(false);
                    return new Result(true, process.ExitCode, output.Trim(), error.Trim());
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    try { process.WaitForExit(2000); } catch { }
                    return new Result(false, -1, string.Empty, $"Timed out after {timeoutMs}ms.");
                }
            }
            catch (Exception ex)
            {
                return new Result(false, -1, string.Empty, ex.Message);
            }
            finally
            {
                process?.Dispose();
            }
        }

        internal static Task<Result> RunPowerShellAsync(string script, int timeoutMs = 15000, CancellationToken token = default) =>
            RunAsync("powershell.exe", new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-NonInteractive", "-Command", "-"
            }, script, timeoutMs, token);
    }
}
