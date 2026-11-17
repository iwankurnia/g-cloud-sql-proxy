using System;
using System.Collections.Generic;
using System.IO;

namespace CloudSqlProxyManager
{
    /// <summary>
    /// The instances offered in the dropdowns before anything has been saved.
    /// Edit the list and rebuild, or, without rebuilding, put a file named
    /// CloudSqlProxyManager.instances.txt beside the program or in its settings
    /// folder, with one project:region:instance name per line.
    /// </summary>
    public static class KnownInstances
    {
        private const string ExtraFile = "CloudSqlProxyManager.instances.txt";

        private static readonly string[] BuiltIn = new string[]
        {
            "hms-tpa-system:asia-southeast2:hms-id-db",
            "hms-tpa-system:asia-southeast2:hms-id-db-replica",
            "hms-tpa-system:asia-southeast2:hms-id-db-replica-2",
            "hms-tpa-system:asia-southeast2:hms-id-db-dev-finance"
        };

        /// <summary>Each entry is { project, region, instance }.</summary>
        public static List<string[]> All()
        {
            List<string[]> result = new List<string[]>();
            foreach (string name in BuiltIn) Add(result, name);
            foreach (string dir in new string[] { Platform.AppDir, Platform.ConfigDir })
            {
                try
                {
                    string path = Path.Combine(dir, ExtraFile);
                    if (File.Exists(path))
                    {
                        foreach (string line in File.ReadAllLines(path)) Add(result, line);
                    }
                }
                catch
                {
                    // The extra file is optional.
                }
            }
            return result;
        }

        private static void Add(List<string[]> list, string connectionName)
        {
            string text = (connectionName ?? "").Trim();
            if (text.Length == 0 || text.StartsWith("#")) return;
            string[] parts = text.Split(':');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) return;
            foreach (string[] existing in list)
            {
                if (string.Equals(string.Join(":", existing), text, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(parts);
        }
    }
}
