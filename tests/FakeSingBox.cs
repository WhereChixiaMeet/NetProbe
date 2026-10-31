using System;
using System.Threading;
class FakeSingBox
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "check") return 0;
        // Offline process-lifecycle double: no sockets, TUN, routes or DNS changes.
        Thread.Sleep(Timeout.Infinite); return 0;
    }
}
