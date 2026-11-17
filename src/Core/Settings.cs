using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CloudSqlProxyManager
{
    public class ConnectionProfile
    {
        public string Project { get; set; }
        public string Region { get; set; }
        public string Instance { get; set; }
        public int Port { get; set; }
        public string CredentialsFile { get; set; }
        public string ExtraArgs { get; set; }

        public string ConnectionName()
        {
            return Project + ":" + Region + ":" + Instance;
        }

        /// <summary>One saved entry per instance; it remembers the port last used for it.</summary>
        public string Key()
        {
            return ConnectionName().ToLowerInvariant();
        }

        public override string ToString()
        {
            return Instance + "   ·   port " + Port + "   ·   " + Project;
        }
    }

    public class AppSettings
    {
        public string ProxyPath { get; set; }
        public bool AutoReconnect { get; set; }
        /// <summary>Tuck the window away in the notification area once the tunnel is up.</summary>
        public bool HideOnConnect { get; set; }
        public List<ConnectionProfile> Connections { get; set; }

        public AppSettings()
        {
            AutoReconnect = true;
            HideOnConnect = true;
            Connections = new List<ConnectionProfile>();
        }
    }

    /// <summary>
    /// Saved connections, as a small JSON file in Platform.ConfigDir. Every save
    /// re-reads the file first, so two running copies of the app do not
    /// overwrite each other's saved connections.
    /// </summary>
    public static class SettingsStore
    {
        private const string FileName = "CloudSqlProxyManager.settings.json";
        private const int MaxConnections = 20;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static string FilePath()
        {
            return Path.Combine(Platform.ConfigDir, FileName);
        }

        public static AppSettings Load()
        {
            AppSettings settings = null;
            try
            {
                string path = FilePath();
                if (File.Exists(path))
                {
                    settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8), Json);
                }
            }
            catch
            {
                // A damaged settings file must never stop the app from opening.
            }

            if (settings == null) settings = new AppSettings();
            if (settings.Connections == null) settings.Connections = new List<ConnectionProfile>();
            settings.Connections.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Project)
                || string.IsNullOrWhiteSpace(p.Region) || string.IsNullOrWhiteSpace(p.Instance));
            foreach (ConnectionProfile p in settings.Connections)
            {
                if (p.Port < 1 || p.Port > 65535) p.Port = 3306;
            }
            return settings;
        }

        /// <summary>Puts the connection first in the saved list and stores the app-wide options.</summary>
        public static AppSettings SaveConnection(ConnectionProfile profile, string proxyPath, bool autoReconnect, bool hideOnConnect)
        {
            AppSettings settings = Load();
            string key = profile.Key();
            settings.Connections.RemoveAll(p => p.Key() == key);
            settings.Connections.Insert(0, profile);
            if (settings.Connections.Count > MaxConnections)
            {
                settings.Connections.RemoveRange(MaxConnections, settings.Connections.Count - MaxConnections);
            }
            settings.ProxyPath = proxyPath;
            settings.AutoReconnect = autoReconnect;
            settings.HideOnConnect = hideOnConnect;
            Save(settings);
            return settings;
        }

        public static AppSettings RemoveConnection(string key)
        {
            AppSettings settings = Load();
            settings.Connections.RemoveAll(p => p.Key() == key);
            Save(settings);
            return settings;
        }

        private static void Save(AppSettings settings)
        {
            try
            {
                string path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json), new UTF8Encoding(false));
                File.Move(temp, path, true);
            }
            catch
            {
                // Saved connections are a convenience; failing to store them must not block connecting.
            }
        }
    }
}
