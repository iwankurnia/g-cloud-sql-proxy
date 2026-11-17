using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CloudSqlProxyManager
{
    /// <summary>Answers "is this local port taken, and by which program?" without opening it.</summary>
    public static class PortInspector
    {
        public static bool IsInUse(int port)
        {
            try
            {
                foreach (IPEndPoint ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                {
                    if (ep.Port == port) return true;
                }
                return false;
            }
            catch
            {
                // Not available on this system: fall back to trying to open it.
                return !ProxyController.IsPortFree(port);
            }
        }

        /// <summary>First free port at or above the given one, or 0 if none was found nearby.</summary>
        public static int NextFree(int from)
        {
            for (int port = Math.Max(1, from); port <= 65535 && port < from + 200; port++)
            {
                if (!IsInUse(port)) return port;
            }
            return 0;
        }

        /// <summary>
        /// For example "mysqld.exe (PID 1234)", or null when the owner is unknown.
        /// On Linux and macOS this runs a system tool (ss, lsof), so call it off
        /// the UI thread.
        /// </summary>
        public static string DescribeOwner(int port)
        {
            try
            {
                if (Platform.IsWindows) return DescribeWindows(port);
                if (Platform.IsLinux) return DescribeLinux(port);
                if (Platform.IsMac) return DescribeMac(port);
            }
            catch
            {
            }
            return null;
        }

        // ------------------------------------------------------------ Windows

        private static string DescribeWindows(int port)
        {
            int pid = WindowsOwnerPid(port);
            if (pid <= 0) return null;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return p.ProcessName + ".exe (PID " + pid + ")";
                }
            }
            catch
            {
                return "PID " + pid;
            }
        }

        private static int WindowsOwnerPid(int port)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (size <= 0) return 0;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return 0;
                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                IntPtr row = IntPtr.Add(buffer, 4);
                for (int i = 0; i < count; i++)
                {
                    MIB_TCPROW_OWNER_PID entry = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(row);
                    // The port is stored in network byte order in the low 16 bits.
                    int local = (int)(((entry.LocalPort & 0xFF) << 8) | ((entry.LocalPort >> 8) & 0xFF));
                    if (local == port) return (int)entry.OwningPid;
                    row = IntPtr.Add(row, rowSize);
                }
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;
            public uint RemoteAddr;
            public uint RemotePort;
            public uint OwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);

        // -------------------------------------------------------------- Linux

        private static string DescribeLinux(int port)
        {
            // ss names the owner only for the user's own processes, which is the usual case.
            string output = Run("ss", "-ltnpH", "sport = :" + port);
            if (output == null) return null;
            Match m = Regex.Match(output, "users:\\(\\(\"([^\"]+)\",pid=(\\d+)");
            return m.Success ? m.Groups[1].Value + " (PID " + m.Groups[2].Value + ")" : null;
        }

        // -------------------------------------------------------------- macOS

        private static string DescribeMac(int port)
        {
            // -F prints one field per line: p<pid>, then c<command>.
            string output = Run("lsof", "-nP", "-iTCP:" + port, "-sTCP:LISTEN", "-Fpc");
            if (output == null) return null;
            string pid = null, name = null;
            foreach (string line in output.Split('\n'))
            {
                if (line.Length < 2) continue;
                if (line[0] == 'p' && pid == null) pid = line.Substring(1).Trim();
                else if (line[0] == 'c' && name == null) name = line.Substring(1).Trim();
            }
            if (pid == null) return null;
            return name != null ? name + " (PID " + pid + ")" : "PID " + pid;
        }

        private static string Run(string program, params string[] args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(program);
                foreach (string a in args) psi.ArgumentList.Add(a);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(1500))
                    {
                        try { p.Kill(true); } catch { }
                        return null;
                    }
                    return output;
                }
            }
            catch
            {
                return null; // the tool is not installed
            }
        }
    }
}
