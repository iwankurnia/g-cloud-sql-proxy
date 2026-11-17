using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

// Stand-in for cloud-sql-proxy so the controller and the window can be tested
// without Google credentials. It prints what the real proxy prints and stays alive.
//   --fail    print an error and exit with code 1 (bad credentials, wrong instance)
//   --quiet   never print the "ready" line (older proxy builds)
internal static class FakeProxy
{
    private static int Main(string[] args)
    {
        bool fail = false, quiet = false;
        string port = "?";
        for (int i = 0; i < args.Length; i++)
        {
            Console.Out.WriteLine("arg[" + i + "]=" + args[i]);
            if (args[i] == "--fail") fail = true;
            if (args[i] == "--quiet") quiet = true;
            if (args[i].StartsWith("--port=")) port = args[i].Substring(7);
        }
        Console.Out.Flush();

        if (fail)
        {
            Console.Error.WriteLine("The proxy has encountered a terminal error: fake failure, could not find default credentials");
            Console.Error.Flush();
            return 1;
        }

        // Hold the local port like the real proxy does, when a usable one was given.
        int number;
        if (int.TryParse(port, out number) && number > 0 && number < 65536)
        {
            try
            {
                new TcpListener(IPAddress.Loopback, number).Start();
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine("The proxy has encountered a terminal error: unable to start: [fake] listen tcp 127.0.0.1:"
                    + number + ": " + ex.Message);
                Console.Error.Flush();
                return 1;
            }
        }

        Console.Out.WriteLine("Listening on 127.0.0.1:" + port);
        if (!quiet) Console.Out.WriteLine("The proxy has started successfully and is ready for new connections!");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
