using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace CloudSqlProxyManager
{
    /// <summary>
    /// Google's cloud-sql-proxy, carried inside the app as a gzip resource for
    /// the system it was built for (added by the build when vendor\ holds the
    /// binary). A program can only run from a file, so on first use it is
    /// unpacked to Platform.DataDir and reused from there afterwards.
    /// </summary>
    public static class BundledProxy
    {
        private const string DataResource = "CloudSqlProxyManager.proxy.gz";
        private const string InfoResource = "CloudSqlProxyManager.proxy.info";

        private static readonly object Sync = new object();
        private static Assembly _carrier;
        private static bool _infoLoaded;

        /// <summary>The program that embeds the proxy: the entry assembly, not this library.</summary>
        private static Assembly Carrier
        {
            get
            {
                if (_carrier == null)
                {
                    Assembly entry = Assembly.GetEntryAssembly();
                    _carrier = entry != null && entry.GetManifestResourceInfo(InfoResource) != null ? entry : Assembly.GetExecutingAssembly();
                }
                return _carrier;
            }
        }
        private static string _version;
        private static string _sha256;
        private static long _size;
        private static string _verifiedPath;

        public static bool IsAvailable
        {
            get { LoadInfo(); return _sha256 != null; }
        }

        /// <summary>For example "v2.28.0", or null when no proxy is bundled.</summary>
        public static string Version
        {
            get { LoadInfo(); return _version; }
        }

        /// <summary>Returns the path of the unpacked proxy, unpacking it first if needed.</summary>
        public static string EnsureExtracted()
        {
            LoadInfo();
            if (_sha256 == null) throw new InvalidOperationException("This build has no bundled proxy.");

            lock (Sync)
            {
                if (_verifiedPath != null && File.Exists(_verifiedPath)) return _verifiedPath;

                string name = "cloud-sql-proxy-" + _version + "-" + _sha256.Substring(0, 8) + Platform.ExeSuffix;
                Exception last = null;
                foreach (string dir in new string[] { Platform.DataDir, Platform.TempDir })
                {
                    try
                    {
                        string path = Path.Combine(dir, name);
                        // Reuse only an exact copy; anything else (half-written, altered) is replaced.
                        if (!File.Exists(path) || new FileInfo(path).Length != _size || HashOf(path) != _sha256)
                        {
                            Extract(dir, path);
                        }
                        MakeExecutable(path);
                        _verifiedPath = path;
                        return path;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }
                throw new IOException("The built-in proxy could not be unpacked: " + (last != null ? last.Message : "no writable folder"), last);
            }
        }

        private static void Extract(string dir, string path)
        {
            Directory.CreateDirectory(dir);
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (Stream resource = Carrier.GetManifestResourceStream(DataResource))
                using (GZipStream unzip = new GZipStream(resource, CompressionMode.Decompress))
                using (FileStream output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    unzip.CopyTo(output);
                }
                if (HashOf(temp) != _sha256) throw new InvalidDataException("the unpacked file does not match its checksum");

                try
                {
                    File.Move(temp, path, true);
                }
                catch (IOException)
                {
                    // Another copy of the app unpacked it at the same moment, or is
                    // running it right now. Its file is fine if it matches.
                    if (!File.Exists(path) || HashOf(path) != _sha256) throw;
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static void MakeExecutable(string path)
        {
            if (Platform.IsWindows) return;
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode wanted = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (wanted != mode) File.SetUnixFileMode(path, wanted);
        }

        private static string HashOf(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
        }

        private static void LoadInfo()
        {
            lock (Sync)
            {
                if (_infoLoaded) return;
                _infoLoaded = true;
                try
                {
                    Assembly assembly = Carrier;
                    using (Stream stream = assembly.GetManifestResourceStream(InfoResource))
                    {
                        if (stream == null || assembly.GetManifestResourceInfo(DataResource) == null) return;
                        string version = null, sha = null;
                        long size = 0;
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                int eq = line.IndexOf('=');
                                if (eq <= 0) continue;
                                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                                if (key == "version") version = value;
                                else if (key == "sha256") sha = value.ToLowerInvariant();
                                else if (key == "size") long.TryParse(value, out size);
                            }
                        }
                        if (sha != null && sha.Length == 64 && size > 0)
                        {
                            _version = string.IsNullOrEmpty(version) ? "unknown" : version;
                            _sha256 = sha;
                            _size = size;
                        }
                    }
                }
                catch
                {
                    // Treated as "not bundled".
                }
            }
        }
    }
}
