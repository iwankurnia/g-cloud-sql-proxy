using System;
using System.IO;

namespace CloudSqlProxyManager
{
    /// <summary>Finds a cloud-sql-proxy program that is not the built-in one.</summary>
    public static class ProxyLocator
    {
        /// <summary>Set to a proxy program to use it instead of the built-in one (used by the UI tests).</summary>
        public const string OverrideVariable = "CLOUDSQLPROXY_EXE";

        public static string FromEnvironment()
        {
            string path = Environment.GetEnvironmentVariable(OverrideVariable);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }

        /// <summary>A proxy beside the app or on PATH, or "" when none is found.</summary>
        public static string Detect()
        {
            try
            {
                foreach (string file in Directory.GetFiles(Platform.AppDir, "cloud-sql-proxy*"))
                {
                    if (Platform.IsWindows ? file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) : !file.Contains('.')) return file;
                }
            }
            catch
            {
            }

            string[] names = Platform.IsWindows
                ? new string[] { "cloud-sql-proxy.exe", "cloud-sql-proxy.x64.exe" }
                : new string[] { "cloud-sql-proxy" };
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                if (dir.Trim().Length == 0) continue;
                foreach (string name in names)
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch
                    {
                    }
                }
            }
            return "";
        }
    }
}
