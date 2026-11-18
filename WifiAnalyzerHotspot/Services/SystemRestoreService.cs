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
            var state = Load();
            if (state.Scripts.Count == 0) return;

            var completed = new List<string>();
            foreach (var entry in state.Scripts)
            {
                if (TryRun(entry.Value, "RunPending")) completed.Add(entry.Key);
            }
            if (completed.Count == 0) return;

            lock (Lock)
            {
                var current = Load();
                foreach (string key in completed) current.Scripts.Remove(key);
                Save(current);
            }
        }

        /// <summary>
        /// Restores and forgets one area after an explicit user opt-out (e.g. Secure Connect OFF).
        /// </summary>
        public static void Release(string key)
        {
            string? script;
            lock (Lock)
            {
                if (!Load().Scripts.TryGetValue(key, out script) || string.IsNullOrWhiteSpace(script)) return;
            }
            if (!TryRun(script, $"Release:{key}")) return;

            lock (Lock)
            {
                var state = Load();
                state.Scripts.Remove(key);
                Save(state);
            }
        }

        private static bool TryRun(string script, string label)
        {
            try
            {
                var result = ProcessRunner.RunPowerShellAsync(script, 10000).GetAwaiter().GetResult();
                if (!result.Completed)
                {
                    Debug.WriteLine($"SystemRestore.{label}: {result.Error}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SystemRestore.{label}: {ex.Message}");
                return false;
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
