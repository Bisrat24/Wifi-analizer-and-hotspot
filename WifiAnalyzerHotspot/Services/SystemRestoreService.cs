using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Persists PowerShell restore snippets BEFORE changing global OS settings and replays them
    /// at shutdown and on the next startup, so a crash or hard kill cannot leave them changed.
    /// </summary>
    internal static class SystemRestoreService
    {
        private sealed class State
        {
            public Dictionary<string, string> Scripts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WifiAnalyzerHotspot", "pending_restore.json");
        private static readonly object Lock = new();

        public static void Stage(string key, string restoreScript)
        {
            if (string.IsNullOrWhiteSpace(restoreScript)) return;
            lock (Lock)
            {
                try
                {
                    var state = Load();
                    state.Scripts[key] = restoreScript;
                    Save(state);
                }
                catch (Exception ex) { Debug.WriteLine($"SystemRestore.Stage: {ex.Message}"); }
            }
        }

        public static void RunPending()
        {
            State state;
            lock (Lock)
            {
                state = Load();
                if (state.Scripts.Count == 0) return;
                Save(new State());
            }

            foreach (var script in state.Scripts.Values)
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command -")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    if (p == null) continue;
                    p.StandardInput.WriteLine(script);
                    p.StandardInput.Close();
                    if (!p.WaitForExit(10000)) p.Kill(true);
                }
                catch (Exception ex) { Debug.WriteLine($"SystemRestore.RunPending: {ex.Message}"); }
            }
        }

        internal static string Escape(string? value) => (value ?? string.Empty).Replace("'", "''");

        private static State Load()
        {
            try
            {
                if (File.Exists(StatePath))
                    return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State();
            }
            catch { }
            return new State();
        }

        private static void Save(State state)
        {
            var dir = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(dir);
            string tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, StatePath, overwrite: true);
        }
    }
}
