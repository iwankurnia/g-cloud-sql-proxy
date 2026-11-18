using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CloudSqlProxyManager
{
    public enum ProxyState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting
    }

    public class ProxyOptions
    {
        public string ProxyPath;
        public string ConnectionName;
        public int Port;
        public string CredentialsFile;
        public string ExtraArgs;
        /// <summary>Log in to the database as the IAM principal: the proxy's --auto-iam-authn.</summary>
        public bool AutoIamAuthn;
    }

    /// <summary>
    /// Owns the cloud-sql-proxy child process: starts it, watches it, and brings
    /// it back when it dies or when the network drops, until Disconnect is called.
    ///
    /// Drop detection deliberately never opens a connection through the tunnel:
    /// half-open connections count as failed handshakes on MySQL and can get the
    /// client host blocked (max_connect_errors). Instead it watches
    ///   1. the proxy process itself (exit = drop), and
    ///   2. reachability of the Cloud SQL Admin API host, which the proxy needs.
    /// </summary>
    public sealed class ProxyController : IDisposable
    {
        /// <summary>Raised on a background thread. Handlers must not block.</summary>
        public event Action<ProxyState, string> StateChanged;

        /// <summary>Raised on a background thread. Handlers must not block.</summary>
        public event Action<string> Log;

        // Tunables. Public so the tests can shorten them.
        public bool AutoReconnect = true;
        public string ProbeHost = "sqladmin.googleapis.com";
        public int ProbePort = 443;
        public int ProbeIntervalMs = 10000;
        public int ProbeRetryMs = 3000;
        public int ProbeTimeoutMs = 4000;
        public int ProbeFailuresBeforeDrop = 2;
        public int WaitForNetworkMs = 5000;
        public int ReadyFallbackMs = 10000;
        public int[] BackoffMs = new int[] { 2000, 5000, 10000, 20000, 30000 };

        private const string ReadyMarker = "ready for new connections";

        private readonly object _sync = new object();
        private readonly Timer _retryTimer;
        private readonly Timer _probeTimer;
        private readonly Timer _readyTimer;
        private readonly ChildProcessJob _job = new ChildProcessJob();

        private ProxyOptions _options;
        private Process _process;
        private int _generation;
        private bool _desired;
        private bool _disposed;
        private ProxyState _state = ProxyState.Disconnected;
        private int _attempt;
        private int _probeFailures;
        private bool _probeBaseline;
        private bool _probeDisabled;
        private bool _waitingForNetwork;
        private string _lastError;
        private string _lastFailure;

        public ProxyController()
        {
            _retryTimer = new Timer(RetryTick, null, Timeout.Infinite, Timeout.Infinite);
            _probeTimer = new Timer(ProbeTick, null, Timeout.Infinite, Timeout.Infinite);
            _readyTimer = new Timer(ReadyTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public ProxyState State
        {
            get { lock (_sync) { return _state; } }
        }

        /// <summary>True from Connect until Disconnect (or a fatal start error).</summary>
        public bool WantsConnection
        {
            get { lock (_sync) { return _desired; } }
        }

        public static bool IsPortFree(int port)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                if (listener != null)
                {
                    try { listener.Stop(); } catch { }
                }
            }
        }

        /// <summary>The argument list as the proxy receives it (used for the log line and the tests).</summary>
        public static string[] BuildArguments(ProxyOptions o)
        {
            var args = new System.Collections.Generic.List<string>();
            args.Add(o.ConnectionName);
            args.Add("--port=" + o.Port);
            if (!string.IsNullOrWhiteSpace(o.CredentialsFile))
            {
                args.Add("--credentials-file=" + o.CredentialsFile.Trim());
            }
            if (o.AutoIamAuthn) args.Add("--auto-iam-authn");
            if (!string.IsNullOrWhiteSpace(o.ExtraArgs))
            {
                foreach (string extra in SplitArgs(o.ExtraArgs)) args.Add(extra);
            }
            return args.ToArray();
        }

        /// <summary>Splits "--a --b="x y"" like a shell would, honouring double quotes.</summary>
        public static string[] SplitArgs(string text)
        {
            var result = new System.Collections.Generic.List<string>();
            var current = new StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in text)
            {
                if (c == '"') { quoted = !quoted; any = true; }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
                }
                else { current.Append(c); any = true; }
            }
            if (any) result.Add(current.ToString());
            return result.ToArray();
        }

        public static string Describe(ProxyOptions o)
        {
            var sb = new StringBuilder(System.IO.Path.GetFileName(o.ProxyPath));
            foreach (string a in BuildArguments(o))
            {
                sb.Append(' ');
                sb.Append(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Commands
        // ------------------------------------------------------------------

        public void Connect(ProxyOptions options)
        {
            lock (_sync)
            {
                if (_disposed || _desired) return;
                _options = options;
                _desired = true;
                _attempt = 0;
                _probeFailures = 0;
                _probeBaseline = false;
                _probeDisabled = false;
                _waitingForNetwork = false;
                _lastFailure = null;
                StartProcessLocked();
            }
        }

        public void Disconnect()
        {
            lock (_sync)
            {
                bool wasActive = _desired || _state != ProxyState.Disconnected;
                _desired = false;
                StopTimersLocked();
                KillProcessLocked();
                if (wasActive)
                {
                    EmitLog("Disconnected.");
                    SetState(ProxyState.Disconnected, null);
                }
            }
        }

        /// <summary>Restart a healthy tunnel, e.g. after the computer wakes from sleep.</summary>
        public void RestartIfConnected(string reason)
        {
            lock (_sync)
            {
                if (!_desired || !AutoReconnect || _state != ProxyState.Connected) return;
                EmitLog(reason);
                StopTimersLocked();
                KillProcessLocked();
                _attempt = 0;
                _waitingForNetwork = false;
                SetState(ProxyState.Reconnecting, reason);
                Arm(_retryTimer, 1000);
            }
        }

        /// <summary>Ask for an early network check, e.g. when the OS reports a network change.</summary>
        public void CheckNetworkNow()
        {
            lock (_sync)
            {
                if (!_desired || !AutoReconnect || _probeDisabled || _state != ProxyState.Connected) return;
                Arm(_probeTimer, 500);
            }
        }

        public void Dispose()
        {
            Disconnect();
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _retryTimer.Dispose();
            _probeTimer.Dispose();
            _readyTimer.Dispose();
            _job.Dispose();
        }

        // ------------------------------------------------------------------
        // Process lifecycle
        // ------------------------------------------------------------------

        private void StartProcessLocked()
        {
            _generation++;
            int gen = _generation;
            _lastError = null;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _options.ProxyPath;
            foreach (string a in BuildArguments(_options)) psi.ArgumentList.Add(a);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            Process p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (s, e) => OnLine(gen, e.Data, false);
            p.ErrorDataReceived += (s, e) => OnLine(gen, e.Data, true);
            p.Exited += (s, e) => OnExited(p, gen);

            EmitLog("> " + Describe(_options));
            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                // The executable is missing or cannot run: retrying will not help.
                if (!(ex is Win32Exception) && !(ex is InvalidOperationException)) throw;
                p.Dispose();
                _desired = false;
                StopTimersLocked();
                string why = "Could not start the proxy program: " + ex.Message;
                EmitLog(why);
                SetState(ProxyState.Disconnected, why);
                return;
            }

            _job.Add(p);
            _process = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (_attempt == 0)
            {
                SetState(ProxyState.Connecting, "Starting the proxy…");
            }
            else
            {
                // Keep the reason on screen while retrying, so it does not flicker away.
                SetState(ProxyState.Reconnecting, "Starting the proxy again (attempt " + _attempt + ")…"
                    + (_lastFailure != null ? " Last result: " + _lastFailure : ""));
            }
            Arm(_readyTimer, ReadyFallbackMs);
        }

        /// <summary>Stops the current process on purpose; its exit will be ignored.</summary>
        private void KillProcessLocked()
        {
            _generation++;
            Process p = _process;
            _process = null;
            if (p == null) return;
            try
            {
                if (!p.HasExited) p.Kill(true);
            }
            catch
            {
                // Already gone.
            }
            // Disposed by OnExited once the exit has been observed.
        }

        private void OnLine(int gen, string line, bool isError)
        {
            if (line == null) return;
            line = StripTimestamp(line);
            lock (_sync)
            {
                EmitLog(line);
                if (gen != _generation) return;

                if (line.IndexOf(ReadyMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    MarkConnectedLocked();
                    return;
                }

                if (isError || line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _lastError = line.Trim();
                    // A failed dial is the earliest hint that the network has gone.
                    if (_state == ProxyState.Connected && AutoReconnect && !_probeDisabled)
                    {
                        Arm(_probeTimer, 200);
                    }
                }
            }
        }

        /// <summary>The proxy prefixes each line with "2026/11/16 22:02:00 "; the log has its own clock.</summary>
        private static string StripTimestamp(string line)
        {
            if (line.Length > 20 && line[4] == '/' && line[7] == '/' && line[10] == ' '
                && line[13] == ':' && line[16] == ':' && line[19] == ' '
                && char.IsDigit(line[0]) && char.IsDigit(line[18]))
            {
                return line.Substring(20);
            }
            return line;
        }

        private void OnExited(Process p, int gen)
        {
            // WaitForExit() without a timeout also drains the redirected output,
            // so the last error line is in _lastError before we report.
            int code = -1;
            try { p.WaitForExit(); } catch { }
            try { code = p.ExitCode; } catch { }
            try { p.Dispose(); } catch { }

            lock (_sync)
            {
                if (gen != _generation) return; // stopped on purpose
                _process = null;
                if (!_desired) return;

                string why = "The proxy stopped (exit code " + code + ")"
                    + (_lastError != null ? ": " + _lastError.TrimEnd('.', ' ') + "." : ".");
                EmitLog(why);
                _lastFailure = why;

                if (!AutoReconnect)
                {
                    _desired = false;
                    StopTimersLocked();
                    SetState(ProxyState.Disconnected, why);
                    return;
                }
                ScheduleRetryLocked(why);
            }
        }

        private void MarkConnectedLocked()
        {
            if (_state != ProxyState.Connecting && _state != ProxyState.Reconnecting) return;
            bool wasReconnect = _state == ProxyState.Reconnecting;
            _attempt = 0;
            _probeFailures = 0;
            _waitingForNetwork = false;
            _lastFailure = null;
            Arm(_readyTimer, Timeout.Infinite);
            if (wasReconnect) EmitLog("Reconnected.");
            SetState(ProxyState.Connected, null);
            if (AutoReconnect && !_probeDisabled) Arm(_probeTimer, 1000);
        }

        private void ScheduleRetryLocked(string why)
        {
            Arm(_probeTimer, Timeout.Infinite);
            Arm(_readyTimer, Timeout.Infinite);
            int delay = BackoffMs[Math.Min(_attempt, BackoffMs.Length - 1)];
            _attempt++;
            string wait = delay >= 1000 ? (delay / 1000) + " s" : delay + " ms";
            SetState(ProxyState.Reconnecting, why + " Trying again in " + wait + " (attempt " + _attempt + ").");
            Arm(_retryTimer, delay);
        }

        // ------------------------------------------------------------------
        // Timers
        // ------------------------------------------------------------------

        /// <summary>Older proxy builds may not print the ready line; assume ready if still running.</summary>
        private void ReadyTick(object unused)
        {
            lock (_sync)
            {
                if (!_desired || _process == null) return;
                if (_state != ProxyState.Connecting && _state != ProxyState.Reconnecting) return;
                bool alive;
                try { alive = !_process.HasExited; } catch { alive = false; }
                if (!alive) return;
                EmitLog("No ready message from the proxy, but it is still running; treating it as connected.");
                MarkConnectedLocked();
            }
        }

        private void RetryTick(object unused)
        {
            bool probeFirst;
            int gen;
            lock (_sync)
            {
                if (!_desired || _state != ProxyState.Reconnecting || _process != null) return;
                probeFirst = _probeBaseline && !_probeDisabled;
                gen = _generation;
            }

            bool networkUp = !probeFirst || TcpProbe(ProbeHost, ProbePort, ProbeTimeoutMs);

            lock (_sync)
            {
                if (!_desired || _state != ProxyState.Reconnecting || _process != null || gen != _generation) return;
                if (!networkUp)
                {
                    if (!_waitingForNetwork)
                    {
                        _waitingForNetwork = true;
                        EmitLog("No network. Waiting for it to come back.");
                        SetState(ProxyState.Reconnecting, "No network. Waiting for it to come back…");
                    }
                    Arm(_retryTimer, WaitForNetworkMs);
                    return;
                }
                if (_waitingForNetwork)
                {
                    _waitingForNetwork = false;
                    EmitLog("Network is back.");
                }
                StartProcessLocked();
            }
        }

        private void ProbeTick(object unused)
        {
            int gen;
            lock (_sync)
            {
                if (!_desired || !AutoReconnect || _probeDisabled || _state != ProxyState.Connected) return;
                gen = _generation;
            }

            bool ok = TcpProbe(ProbeHost, ProbePort, ProbeTimeoutMs);

            lock (_sync)
            {
                if (!_desired || _state != ProxyState.Connected || gen != _generation) return;
                if (ok)
                {
                    _probeBaseline = true;
                    _probeFailures = 0;
                    Arm(_probeTimer, ProbeIntervalMs);
                    return;
                }

                _probeFailures++;
                if (_probeFailures < ProbeFailuresBeforeDrop)
                {
                    Arm(_probeTimer, ProbeRetryMs);
                    return;
                }

                if (!_probeBaseline)
                {
                    // Never reachable directly (firewall or web proxy in the way):
                    // the check tells us nothing, so stop using it for this session.
                    _probeDisabled = true;
                    EmitLog("Cannot reach " + ProbeHost + " directly, so network monitoring is off for this session. "
                        + "The proxy process is still watched.");
                    return;
                }

                EmitLog("Network connection lost (" + ProbeHost + " unreachable). Stopping the proxy until it is back.");
                KillProcessLocked();
                _attempt = 0;
                _probeFailures = 0;
                _waitingForNetwork = true;
                SetState(ProxyState.Reconnecting, "Network connection lost. Waiting for it to come back…");
                Arm(_retryTimer, WaitForNetworkMs);
            }
        }

        private void StopTimersLocked()
        {
            Arm(_retryTimer, Timeout.Infinite);
            Arm(_probeTimer, Timeout.Infinite);
            Arm(_readyTimer, Timeout.Infinite);
        }

        private void Arm(Timer timer, int dueMs)
        {
            if (_disposed) return;
            try { timer.Change(dueMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        private static bool TcpProbe(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------

        private void SetState(ProxyState state, string detail)
        {
            _state = state;
            Action<ProxyState, string> handler = StateChanged;
            if (handler != null)
            {
                try { handler(state, detail); } catch { }
            }
        }

        private void EmitLog(string line)
        {
            Action<string> handler = Log;
            if (handler != null)
            {
                try { handler(line); } catch { }
            }
        }
    }

    /// <summary>
    /// Makes sure the proxy never outlives the app, so a stray proxy cannot keep
    /// holding the local port. On Windows this is a job object, which kills the
    /// children even if the app itself is killed. On Linux and macOS the child is
    /// killed from this process's exit paths (Dispose, and the UI's shutdown).
    /// </summary>
    internal sealed class ChildProcessJob : IDisposable
    {
        private IntPtr _handle;

        public ChildProcessJob()
        {
            if (!Platform.IsWindows) return;
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero) return;

            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Add(Process process)
        {
            if (_handle == IntPtr.Zero) return;
            try { AssignProcessToJobObject(_handle, process.Handle); } catch { }
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
