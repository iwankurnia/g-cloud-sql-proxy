using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CloudSqlProxyManager;

// Drives ProxyController against the stand-in proxy: connect, crash, network
// drop, failing start, disconnect; then the real bundled proxy on a failing start.
// Runs on Windows, Linux and macOS. The stand-in (fakeproxy) must sit beside
// this program; build.ps1 puts it there.
internal static class ControllerTests
{
    private const string FakeName = "fakeproxy";
    private static string _fakePath;
    private static int _passed, _failed;

    private sealed class Rig : IDisposable
    {
        public readonly ProxyController Controller = new ProxyController();
        public readonly List<string> States = new List<string>();
        public readonly List<string> Lines = new List<string>();
        public string LastDetail;

        public Rig()
        {
            Controller.BackoffMs = new int[] { 200, 400 };
            Controller.ProbeIntervalMs = 300;
            Controller.ProbeRetryMs = 150;
            Controller.ProbeTimeoutMs = 500;
            Controller.WaitForNetworkMs = 300;
            Controller.ReadyFallbackMs = 1500;
            Controller.StateChanged += (s, d) => { lock (States) { States.Add(s.ToString()); LastDetail = d; } };
            Controller.Log += l => { lock (Lines) { Lines.Add(l); } };
        }

        public ProxyOptions Options(string extra)
        {
            ProxyOptions o = new ProxyOptions();
            o.ProxyPath = _fakePath;
            o.ConnectionName = "my-project:asia-southeast2:my-instance";
            o.Port = 45991;
            o.CredentialsFile = Path.Combine(Path.GetTempPath(), "some folder", "key file.json");
            o.ExtraArgs = extra;
            return o;
        }

        public bool Saw(string text)
        {
            lock (Lines)
            {
                foreach (string l in Lines) if (l.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public int Count(string state)
        {
            int n = 0;
            lock (States) { foreach (string s in States) if (s == state) n++; }
            return n;
        }

        public void Dispose()
        {
            Controller.Dispose();
        }
    }

    private static int Main()
    {
        _fakePath = Path.Combine(AppContext.BaseDirectory, FakeName + Platform.ExeSuffix);
        if (!File.Exists(_fakePath))
        {
            Console.WriteLine("Stand-in proxy not found: " + _fakePath);
            return 2;
        }
        Console.WriteLine("Platform: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        KillFakes();

        Run("connects and passes the arguments through", ConnectsAndPassesArguments);
        Run("reconnects when the proxy process dies", ReconnectsAfterCrash);
        Run("disconnect stops the proxy and stops reconnecting", DisconnectStops);
        Run("network drop stops the proxy and restores it when the network returns", NetworkDropAndReturn);
        Run("a failing start keeps retrying with the error shown, until disconnect", FailingStartRetries);
        Run("auto-reconnect off: a crash ends as Disconnected", NoAutoReconnect);
        Run("missing proxy program ends as Disconnected with an error", MissingProgram);
        Run("no ready line: treated as connected after the fallback wait", ReadyFallback);
        Run("unreachable probe host from the start does not cause a false drop", ProbeNeverReachable);
        Run("restart on wake from sleep brings a new proxy up", RestartOnResume);
        Run("extra arguments are split like a shell would", ExtraArgumentSplitting);
        Run("built-in proxy unpacks, matches its checksum and runs", BundledProxyRuns);
        Run("real proxy with a missing key file: its own error is shown, retried, then stopped", RealProxyBadKey);

        KillFakes();
        Console.WriteLine();
        Console.WriteLine(_passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- tests

    private static void ConnectsAndPassesArguments()
    {
        using (Rig rig = new Rig())
        {
            ProxyOptions o = rig.Options("--private-ip");
            rig.Controller.Connect(o);
            WaitState(rig, ProxyState.Connected);
            Check(FakePids().Length == 1, "one proxy process is running");
            Check(WaitFor(() => rig.Saw("arg[3]=--private-ip"), 2000), "all arguments arrived");
            Check(rig.Saw("arg[0]=my-project:asia-southeast2:my-instance"), "instance connection name is argument 0");
            Check(rig.Saw("arg[1]=--port=45991"), "port argument");
            Check(rig.Saw("arg[2]=--credentials-file=" + o.CredentialsFile), "key path with spaces stays one argument");
        }
        Check(WaitFor(() => FakePids().Length == 0, 3000), "disposing the controller stops the proxy");
    }

    private static void ReconnectsAfterCrash()
    {
        using (Rig rig = new Rig())
        {
            rig.Controller.Connect(rig.Options(null));
            WaitState(rig, ProxyState.Connected);
            int first = FakePids()[0];

            Process.GetProcessById(first).Kill();
            Check(WaitFor(() => rig.Count("Reconnecting") >= 1, 3000), "went to Reconnecting");
            Check(WaitFor(() => rig.Controller.State == ProxyState.Connected && FakePids().Length == 1 && FakePids()[0] != first, 5000),
                "Connected again with a new process");

            int second = FakePids()[0];
            Process.GetProcessById(second).Kill();
            Check(WaitFor(() => rig.Controller.State == ProxyState.Connected && FakePids().Length == 1 && FakePids()[0] != second, 5000),
                "Connected again after a second crash");
        }
    }

    private static void DisconnectStops()
    {
        using (Rig rig = new Rig())
        {
            rig.Controller.Connect(rig.Options(null));
            WaitState(rig, ProxyState.Connected);
            rig.Controller.Disconnect();
            Check(rig.Controller.State == ProxyState.Disconnected, "state is Disconnected");
            Check(WaitFor(() => FakePids().Length == 0, 3000), "proxy process is gone");
            Thread.Sleep(1500);
            Check(FakePids().Length == 0, "nothing was restarted after disconnect");
            Check(rig.Count("Reconnecting") == 0, "never reported Reconnecting");
        }
    }

    private static void NetworkDropAndReturn()
    {
        // Reserve a port number, then open the stand-in "network" only after the
        // proxy has been started: a child process can inherit open sockets, and an
        // inherited listener would keep the port answering after net.Stop().
        TcpListener net = new TcpListener(IPAddress.Loopback, 0);
        net.Start();
        int port = ((IPEndPoint)net.LocalEndpoint).Port;
        net.Stop();
        try
        {
            using (Rig rig = new Rig())
            {
                rig.Controller.ProbeHost = "127.0.0.1";
                rig.Controller.ProbePort = port;
                rig.Controller.Connect(rig.Options(null));
                net = new TcpListener(IPAddress.Loopback, port);
                net.Start();
                WaitState(rig, ProxyState.Connected);
                int first = FakePids()[0];
                Thread.Sleep(1800); // let at least one probe succeed (baseline)

                net.Stop(); // the "network" goes away
                Check(WaitFor(() => rig.Controller.State == ProxyState.Reconnecting, 5000), "drop detected");
                Check(WaitFor(() => FakePids().Length == 0, 3000), "proxy stopped while the network is down");
                Thread.Sleep(1200);
                Check(FakePids().Length == 0 && rig.Controller.State == ProxyState.Reconnecting, "waits, does not spin up proxies while offline");

                net = new TcpListener(IPAddress.Loopback, port);
                net.Start(); // the "network" is back
                Check(WaitFor(() => rig.Controller.State == ProxyState.Connected && FakePids().Length == 1 && FakePids()[0] != first, 6000),
                    "reconnected with a new proxy once the network returned");
                Check(rig.Saw("Network is back"), "logged that the network came back");

                rig.Controller.Disconnect();
                net.Stop();
                Thread.Sleep(1200);
                Check(rig.Controller.State == ProxyState.Disconnected && FakePids().Length == 0, "after disconnect a network drop is ignored");
            }
        }
        finally
        {
            try { net.Stop(); } catch { }
        }
    }

    private static void FailingStartRetries()
    {
        using (Rig rig = new Rig())
        {
            rig.Controller.Connect(rig.Options("--fail"));
            Check(WaitFor(() => rig.Count("Reconnecting") >= 3, 6000), "keeps retrying");
            Check(rig.LastDetail != null && rig.LastDetail.IndexOf("could not find default credentials") >= 0,
                "the proxy's own error is shown: " + rig.LastDetail);
            Check(rig.Count("Connected") == 0, "never claimed to be connected");
            rig.Controller.Disconnect();
            int runs = CountStarts(rig);
            Thread.Sleep(1500);
            Check(CountStarts(rig) == runs, "no further attempts after disconnect");
            Check(rig.Controller.State == ProxyState.Disconnected, "state is Disconnected");
        }
    }

    private static void NoAutoReconnect()
    {
        using (Rig rig = new Rig())
        {
            rig.Controller.AutoReconnect = false;
            rig.Controller.Connect(rig.Options(null));
            WaitState(rig, ProxyState.Connected);
            Process.GetProcessById(FakePids()[0]).Kill();
            Check(WaitFor(() => rig.Controller.State == ProxyState.Disconnected, 3000), "ends Disconnected");
            Thread.Sleep(1000);
            Check(FakePids().Length == 0, "not restarted");
            Check(!rig.Controller.WantsConnection, "no longer wants a connection");
        }
    }

    private static void MissingProgram()
    {
        using (Rig rig = new Rig())
        {
            ProxyOptions o = rig.Options(null);
            o.ProxyPath = Path.Combine(AppContext.BaseDirectory, "does-not-exist" + Platform.ExeSuffix);
            rig.Controller.Connect(o);
            Check(rig.Controller.State == ProxyState.Disconnected, "state is Disconnected");
            Check(rig.LastDetail != null && rig.LastDetail.StartsWith("Could not start the proxy program"), "error is explained: " + rig.LastDetail);
            Check(!rig.Controller.WantsConnection, "does not keep retrying");
        }
    }

    private static void ReadyFallback()
    {
        using (Rig rig = new Rig())
        {
            rig.Controller.Connect(rig.Options("--quiet"));
            Thread.Sleep(700);
            Check(rig.Controller.State == ProxyState.Connecting, "still Connecting before the fallback wait is over");
            Check(WaitFor(() => rig.Controller.State == ProxyState.Connected, 4000), "Connected after the fallback wait");
        }
    }

    private static void ProbeNeverReachable()
    {
        TcpListener temp = new TcpListener(IPAddress.Loopback, 0);
        temp.Start();
        int dead = ((IPEndPoint)temp.LocalEndpoint).Port;
        temp.Stop();

        using (Rig rig = new Rig())
        {
            rig.Controller.ProbeHost = "127.0.0.1";
            rig.Controller.ProbePort = dead;
            rig.Controller.Connect(rig.Options(null));
            WaitState(rig, ProxyState.Connected);
            int pid = FakePids()[0];
            Thread.Sleep(2500);
            Check(rig.Controller.State == ProxyState.Connected && FakePids().Length == 1 && FakePids()[0] == pid, "tunnel left alone");
            Check(rig.Saw("network monitoring is off"), "said that network monitoring is off");

            Process.GetProcessById(pid).Kill();
            Check(WaitFor(() => rig.Controller.State == ProxyState.Connected && FakePids().Length == 1 && FakePids()[0] != pid, 5000),
                "process crashes are still recovered");
        }
    }

    private static void RestartOnResume()
    {
        using (Rig rig = new Rig())
        {
            rig.Controller.Connect(rig.Options(null));
            WaitState(rig, ProxyState.Connected);
            int pid = FakePids()[0];
            rig.Controller.RestartIfConnected("The computer woke from sleep. Restarting the proxy.");
            Check(rig.Controller.State == ProxyState.Reconnecting, "goes to Reconnecting");
            Check(WaitFor(() => rig.Controller.State == ProxyState.Connected && FakePids().Length == 1 && FakePids()[0] != pid, 5000),
                "a new proxy is up");
        }
    }

    private static void ExtraArgumentSplitting()
    {
        string[] parts = ProxyController.SplitArgs("--private-ip  --token=\"a b\" --x=1");
        Check(parts.Length == 3 && parts[0] == "--private-ip" && parts[1] == "--token=a b" && parts[2] == "--x=1",
            "quoted spaces stay together: " + string.Join(" | ", parts));
        Check(ProxyController.SplitArgs("   ").Length == 0, "blank text gives no arguments");
    }

    private static void BundledProxyRuns()
    {
        if (!BundledProxy.IsAvailable)
        {
            Console.WriteLine("    skip  this build has no built-in proxy");
            return;
        }

        string path = BundledProxy.EnsureExtracted();
        Check(File.Exists(path), "unpacked to " + path);
        Check(BundledProxy.EnsureExtracted() == path, "second call reuses the same file");

        ProcessStartInfo psi = new ProcessStartInfo(path, "--version");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        using (Process p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            Check(output.IndexOf("cloud-sql-proxy version " + BundledProxy.Version.TrimStart('v')) >= 0,
                "it runs and reports " + BundledProxy.Version + ": " + output);
        }

        // A damaged copy must be replaced, not run. (Delete first: Linux refuses to
        // overwrite a program file that was executed moments ago.)
        File.Delete(path);
        File.WriteAllText(path, "not a program");
        typeof(BundledProxy).GetField("_verifiedPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, null);
        string again = BundledProxy.EnsureExtracted();
        Check(again == path && new FileInfo(path).Length > 1000000, "a damaged copy is detected and unpacked again");
    }

    private static void RealProxyBadKey()
    {
        if (!BundledProxy.IsAvailable)
        {
            Console.WriteLine("    skip  this build has no built-in proxy");
            return;
        }

        using (Rig rig = new Rig())
        {
            ProxyOptions o = rig.Options(null);
            o.ProxyPath = BundledProxy.EnsureExtracted();
            o.CredentialsFile = Path.Combine(AppContext.BaseDirectory, "no-such-key.json");
            rig.Controller.Connect(o);
            Check(WaitFor(() => rig.Count("Reconnecting") >= 2, 15000), "the real proxy exits and is retried");
            string detail = rig.LastDetail ?? "";
            Check(detail.IndexOf("terminal error") >= 0 && detail.IndexOf("no-such-key.json") >= 0,
                "Google's own error text stays on screen while retrying: " + detail);
            Check(detail.IndexOf("2026/") < 0 && !rig.Saw("/11/"), "the proxy's date prefix is stripped from the log");
            Check(rig.Count("Connected") == 0, "never claimed to be connected");
            rig.Controller.Disconnect();
            Check(rig.Controller.State == ProxyState.Disconnected, "disconnect stops the retries");
        }
    }

    // -------------------------------------------------------------- helpers

    private static int CountStarts(Rig rig)
    {
        int n = 0;
        lock (rig.Lines) { foreach (string l in rig.Lines) if (l.StartsWith("> ")) n++; }
        return n;
    }

    private static void WaitState(Rig rig, ProxyState state)
    {
        if (!WaitFor(() => rig.Controller.State == state, 5000))
        {
            throw new Exception("timed out waiting for " + state + " (state is " + rig.Controller.State + ")");
        }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try { if (condition()) return true; } catch { }
            Thread.Sleep(50);
        }
        return false;
    }

    private static int[] FakePids()
    {
        List<int> pids = new List<int>();
        foreach (Process p in Process.GetProcessesByName(FakeName))
        {
            try { if (!p.HasExited) pids.Add(p.Id); } catch { }
            p.Dispose();
        }
        return pids.ToArray();
    }

    private static void KillFakes()
    {
        foreach (Process p in Process.GetProcessesByName(FakeName))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }
    }

    private static bool _testFailed;

    private static void Check(bool condition, string what)
    {
        if (condition)
        {
            Console.WriteLine("    ok    " + what);
        }
        else
        {
            _testFailed = true;
            Console.WriteLine("    FAIL  " + what);
        }
    }

    private static void Run(string name, Action test)
    {
        Console.WriteLine(name);
        _testFailed = false;
        try
        {
            test();
        }
        catch (Exception ex)
        {
            _testFailed = true;
            Console.WriteLine("    FAIL  " + ex.Message);
        }
        KillFakes();
        if (_testFailed) _failed++; else _passed++;
    }
}
