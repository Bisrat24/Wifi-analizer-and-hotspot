using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    public class HotspotTimelineService
    {
        private static readonly string TimelineDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WifiAnalyzerHotspot");
        private static readonly string TimelineFilePath = Path.Combine(TimelineDirectory, "hotspot_timeline.json");

        private static readonly object _fileLock = new();
        private static readonly SemaphoreSlim _saveLock = new(1, 1);
        private readonly ConcurrentDictionary<string, DateTime> _sessionStartTimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _baselineMacs = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<HotspotTimelineEvent> Events { get; } = new();

        public HotspotTimelineService()
        {
            LoadEvents();
        }

        public void SeedConnectedBaseline(IEnumerable<string> initialMacs)
        {
            if (initialMacs == null) return;
            foreach (var mac in initialMacs)
            {
                if (string.IsNullOrWhiteSpace(mac)) continue;
                string cleanMac = HotspotModel.NormalizeMac(mac);
                _baselineMacs[cleanMac] = 1;
                if (!_sessionStartTimes.ContainsKey(cleanMac))
                {
                    _sessionStartTimes[cleanMac] = DateTime.Now;
                }
            }
        }

        public void RecordClientJoin(string macAddress, string hostName, string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress)) return;
            string cleanMac = HotspotModel.NormalizeMac(macAddress);
            _sessionStartTimes[cleanMac] = DateTime.Now;

            // Suppress duplicate "Joined" event spam for baseline-seeded clients already present at startup
            if (_baselineMacs.TryRemove(cleanMac, out _))
            {
                return;
            }

            AddEvent(new HotspotTimelineEvent
            {
                Timestamp = DateTime.Now,
                EventType = "Joined",
                HostName = string.IsNullOrWhiteSpace(hostName) ? "Device" : hostName,
                MacAddress = macAddress,
                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "-" : ipAddress,
                DurationText = "-",
                Details = "Connected to hotspot"
            });
        }

        public void RecordClientLeave(string macAddress, string hostName, string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress)) return;
            string cleanMac = HotspotModel.NormalizeMac(macAddress);

            string durationText = "-";
            if (_sessionStartTimes.TryRemove(cleanMac, out var joinTime))
            {
                var duration = DateTime.Now - joinTime;
                durationText = FormatDuration(duration);
            }

            AddEvent(new HotspotTimelineEvent
            {
                Timestamp = DateTime.Now,
                EventType = "Left",
                HostName = string.IsNullOrWhiteSpace(hostName) ? "Device" : hostName,
                MacAddress = macAddress,
                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "-" : ipAddress,
                DurationText = durationText,
                Details = $"Disconnected (Session: {durationText})"
            });
        }

        public void RecordClientPaused(string macAddress, string hostName, string ipAddress, string durationMinutes)
        {
            AddEvent(new HotspotTimelineEvent
            {
                Timestamp = DateTime.Now,
                EventType = "Paused",
                HostName = string.IsNullOrWhiteSpace(hostName) ? "Device" : hostName,
                MacAddress = macAddress,
                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "-" : ipAddress,
                DurationText = $"{durationMinutes} min",
                Details = $"Internet paused for {durationMinutes} min"
            });
        }

        public void RecordClientResumed(string macAddress, string hostName, string ipAddress, string reason = "Manual")
        {
            AddEvent(new HotspotTimelineEvent
            {
                Timestamp = DateTime.Now,
                EventType = "Resumed",
                HostName = string.IsNullOrWhiteSpace(hostName) ? "Device" : hostName,
                MacAddress = macAddress,
                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "-" : ipAddress,
                DurationText = "-",
                Details = $"Internet access restored ({reason})"
            });
        }

        public void RecordClientBlock(string macAddress, string hostName, string ipAddress)
        {
            AddEvent(new HotspotTimelineEvent
            {
                Timestamp = DateTime.Now,
                EventType = "Blocked",
                HostName = string.IsNullOrWhiteSpace(hostName) ? "Device" : hostName,
                MacAddress = macAddress,
                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "-" : ipAddress,
                DurationText = "-",
                Details = "Device blocked from hotspot"
            });
        }

        public void RecordClientUnblock(string macAddress, string hostName, string ipAddress)
        {
            AddEvent(new HotspotTimelineEvent
            {
                Timestamp = DateTime.Now,
                EventType = "Unblocked",
                HostName = string.IsNullOrWhiteSpace(hostName) ? "Device" : hostName,
                MacAddress = macAddress,
                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "-" : ipAddress,
                DurationText = "-",
                Details = "Device unblocked by administrator"
            });
        }

        public void RecordEvent(string eventType, string hostName, string macAddress, string ipAddress, string details)
        {
            AddEvent(new HotspotTimelineEvent
            {
                Timestamp = DateTime.Now,
                EventType = eventType,
                HostName = string.IsNullOrWhiteSpace(hostName) ? "Device" : hostName,
                MacAddress = string.IsNullOrWhiteSpace(macAddress) ? "-" : macAddress,
                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "-" : ipAddress,
                DurationText = "-",
                Details = details
            });
        }

        private void AddEvent(HotspotTimelineEvent ev)
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                Events.Insert(0, ev);
                while (Events.Count > 500)
                {
                    Events.RemoveAt(Events.Count - 1);
                }
                var snapshot = Events.Take(500).ToList();
                _ = Task.Run(async () => await SaveEventsAsync(snapshot));
            });
        }

        public void ClearEvents()
        {
            Events.Clear();
            _ = Task.Run(async () => await SaveEventsAsync(new List<HotspotTimelineEvent>()));
        }

        public static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes < 1)
                return $"{Math.Max(1, (int)duration.TotalSeconds)}s";
            if (duration.TotalHours < 1)
                return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        public string ExportToCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,Event,Device_Name,IP_Address,MAC_Address,Session_Duration,Details");

            var list = Events.ToList();
            foreach (var ev in list)
            {
                string safeName = EscapeCsv(ev.HostName);
                string safeDetails = EscapeCsv(ev.Details);
                sb.AppendLine($"{ev.FormattedTime},{ev.EventType},{safeName},{ev.IpAddress},{ev.MacAddress},{ev.DurationText},{safeDetails}");
            }

            return sb.ToString();
        }

        public bool ExportToCsv(string filePath)
        {
            try
            {
                string csv = ExportToCsv();
                File.WriteAllText(filePath, csv, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string EscapeCsv(string? val)
        {
            if (string.IsNullOrEmpty(val)) return string.Empty;
            // Neutralize formula injection in spreadsheet viewers (=, +, -, @)
            if (val.StartsWith("=") || val.StartsWith("+") || val.StartsWith("-") || val.StartsWith("@"))
            {
                val = "'" + val;
            }
            if (val.Contains(",") || val.Contains("\"") || val.Contains("\n") || val.Contains("\r"))
            {
                return $"\"{val.Replace("\"", "\"\"")}\"";
            }
            return val;
        }

        private void LoadEvents()
        {
            try
            {
                lock (_fileLock)
                {
                    if (File.Exists(TimelineFilePath))
                    {
                        string json = File.ReadAllText(TimelineFilePath);
                        var loaded = JsonSerializer.Deserialize<List<HotspotTimelineEvent>>(json);
                        if (loaded != null)
                        {
                            foreach (var ev in loaded.Take(500))
                            {
                                Events.Add(ev);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private async Task SaveEventsAsync(List<HotspotTimelineEvent>? snapshot = null)
        {
            await _saveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(TimelineDirectory);
                var listToSave = snapshot ?? Events.Take(500).ToList();
                string json = JsonSerializer.Serialize(listToSave, new JsonSerializerOptions { WriteIndented = true });
                string tempPath = TimelineFilePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
                File.Move(tempPath, TimelineFilePath, overwrite: true);
            }
            catch { }
            finally
            {
                _saveLock.Release();
            }
        }
    }
}
