using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WifiAnalyzerHotspot.Models;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// App-owned Wi-Fi profile store. Windows saved networks are never modified.
    /// </summary>
    internal static class SecureConnectProfileStore
    {
        private static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WifiAnalyzerHotspot", "secure_connect_profiles.json");
        private static readonly object Lock = new();
        private static readonly string ImportedMarker = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WifiAnalyzerHotspot", "secure_connect_imported.flag");

        internal static bool IsImported => File.Exists(ImportedMarker);

        internal static void MarkImported()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ImportedMarker)!);
                File.WriteAllText(ImportedMarker, DateTime.UtcNow.ToString("o"));
            }
            catch { }
        }

        internal static List<SecureConnectProfile> Load()
        {
            lock (Lock)
            {
                try
                {
                    if (!File.Exists(Path)) return new List<SecureConnectProfile>();
                    var list = JsonSerializer.Deserialize<List<SecureConnectProfile>>(File.ReadAllText(Path))
                               ?? new List<SecureConnectProfile>();
                    foreach (var profile in list)
                        profile.Password = DpapiSecret.Unprotect(profile.Password);
                    return list;
                }
                catch { return new List<SecureConnectProfile>(); }
            }
        }

        internal static void Save(List<SecureConnectProfile> profiles)
        {
            lock (Lock)
            {
                try
                {
                    var stored = profiles.Select(p => new SecureConnectProfile
                    {
                        Ssid = p.Ssid,
                        AuthType = p.AuthType,
                        Encryption = p.Encryption,
                        IsHidden = p.IsHidden,
                        Password = DpapiSecret.Protect(p.Password),
                        LastConnected = p.LastConnected
                    }).ToList();

                    var dir = System.IO.Path.GetDirectoryName(Path)!;
                    Directory.CreateDirectory(dir);
                    string tmp = Path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(stored));
                    File.Move(tmp, Path, overwrite: true);
                }
                catch { }
            }
        }

        internal static SecureConnectProfile? Find(string ssid)
        {
            return Load().FirstOrDefault(p => p.Ssid.Equals(ssid, StringComparison.OrdinalIgnoreCase));
        }

        internal static void Upsert(SecureConnectProfile profile)
        {
            var list = Load();
            list.RemoveAll(p => p.Ssid.Equals(profile.Ssid, StringComparison.OrdinalIgnoreCase));
            list.Add(profile);
            Save(list);
        }

        internal static void Remove(string ssid)
        {
            var list = Load();
            list.RemoveAll(p => p.Ssid.Equals(ssid, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }
    }
}
