using System;
using System.IO;

namespace CloudSqlProxyManager
{
    /// <summary>Where things live on each operating system.</summary>
    public static class Platform
    {
        private const string AppFolder = "CloudSqlProxyManager";

        public static bool IsWindows { get { return OperatingSystem.IsWindows(); } }
        public static bool IsLinux { get { return OperatingSystem.IsLinux(); } }
        public static bool IsMac { get { return OperatingSystem.IsMacOS(); } }

        public static string ExeSuffix { get { return IsWindows ? ".exe" : ""; } }

        /// <summary>The folder holding the running program.</summary>
        public static string AppDir
        {
            get
            {
                string path = Environment.ProcessPath;
                string dir = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
                return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
            }
        }

        /// <summary>
        /// Where settings are kept. Windows: beside the .exe, so the app stays
        /// portable (falling back to %APPDATA% in a read-only folder). Linux:
        /// ~/.config/CloudSqlProxyManager. macOS: ~/Library/Application Support/
        /// CloudSqlProxyManager, because an .app bundle must not be written into.
        /// </summary>
        public static string ConfigDir
        {
            get
            {
                if (IsWindows && CanWriteTo(AppDir)) return AppDir;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);
            }
        }

        /// <summary>Where the built-in proxy is unpacked.</summary>
        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder); }
        }

        public static string TempDir
        {
            get { return Path.Combine(Path.GetTempPath(), AppFolder); }
        }

        private static bool CanWriteTo(string directory)
        {
            try
            {
                string probe = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
