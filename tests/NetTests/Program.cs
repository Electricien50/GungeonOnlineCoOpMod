using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using GungeonOnline.Net;

static class Program
{
    static readonly Stopwatch sw = Stopwatch.StartNew();
    static double Now() => sw.Elapsed.TotalSeconds;
    static int failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static Handshake Hs(string mod = "1.0", string hash = "abc") => new Handshake { ModVersion = mod, GameHash = hash, PlayerName = "test" };

    static IDatagramSocket Sock(float lat, float jit, float loss, int seed) =>
        lat == 0 && loss == 0 ? new UdpDatagramSocket(0) : new SimulatedLinkSocket(new UdpDatagramSocket(0), Now, lat, jit, loss, seed);

    static int Port(IDatagramSocket s) => ((IPEndPoint)s.LocalEndPoint).Port;

    /// <summary>Runs both nodes at ~60 Hz frames until done() or timeout.</summary>
    static void Run(Func<bool> done, double seconds, params NetNode[] nodes)
    {
        double end = Now() + seconds;
        while (Now() < end && !done())
        {
            foreach (var n in nodes) n.Poll(Now());
            foreach (var n in nodes) n.Flush(Now());
            Thread.Sleep(16);
        }
    }

    static void Reliability(float lat, float jit, float loss)
    {
        var host = new NetNode(Sock(lat, jit, loss, 11), true, 2, Hs());
        var client = new NetNode(Sock(lat, jit, loss, 22), false, 2, Hs());
        Connection toHost = null;
        client.Connected += c => toHost = c;
        var got = new List<int>();
        int bigOk = 0, unreliableGot = 0;
        host.Message += (c, ch, data, d) =>
        {
            if (d == Delivery.Unreliable) { unreliableGot++; return; }
            if (ch == 2) { bigOk += data.Length == 50000 && data[49999] == 0x5A ? 1 : 0; return; }
            got.Add(BitConverter.ToInt32(data, 0));
        };
        client.Connect(new IPEndPoint(IPAddress.Loopback, Port(host.Socket)), Now());
        Run(() => toHost != null, 10, host, client);
        Check(toHost != null, $"connect under {lat}ms/{loss}% loss");
        if (toHost == null) return;

        const int N = 3000;
        var big = new byte[50000]; big[49999] = 0x5A;
        int sent = 0;
        double end = Now() + 30;
        while (Now() < end && (got.Count < N || bigOk < 3))
        {
            for (int i = 0; i < 50 && sent < N; i++)
            {
                toHost.Send(1, BitConverter.GetBytes(sent++), Delivery.ReliableOrdered);
                if (sent % 1000 == 0) toHost.Send(2, big, Delivery.ReliableOrdered); // three big ones, interleaved
            }
            toHost.Send(3, new byte[17], Delivery.Unreliable);
            host.Poll(Now()); client.Poll(Now());
            host.Flush(Now()); client.Flush(Now());
            Thread.Sleep(16);
        }
        bool inOrder = got.Count == N;
        for (int i = 0; i < got.Count && inOrder; i++) inOrder = got[i] == i;
        Check(inOrder, $"{N} reliable messages exactly once, in order ({got.Count} got) under {lat}ms/{jit}ms jitter/{loss}% loss");
        Check(bigOk == 3, $"3 fragmented 50 KB messages reassembled ({bigOk})");
        Check(unreliableGot > 0, $"unreliable channel delivers ({unreliableGot})");
        host.UpdateStats(Now()); client.UpdateStats(Now());
        Console.WriteLine($"     client rtt={toHost.RttMs:F1}ms var={toHost.RttVarMs:F1} loss={toHost.LossPercent:F1}% resends={toHost.Resends} sent={toHost.PacketsSent}");
        if (lat > 0) Check(Math.Abs(toHost.RttMs - lat) < lat * 0.5 + 20, $"RTT estimate near {lat}ms");
        client.Dispose(); host.Dispose();
    }

    static void Mismatch()
    {
        var host = new NetNode(Sock(0, 0, 0, 1), true, 2, Hs("1.0"));
        var client = new NetNode(Sock(0, 0, 0, 2), false, 2, Hs("0.9"));
        string reason = null;
        client.Rejected += r => reason = r;
        client.Connect(new IPEndPoint(IPAddress.Loopback, Port(host.Socket)), Now());
        Run(() => reason != null, 5, host, client);
        Check(reason != null && reason.Contains("versions"), "mod version mismatch rejected: " + reason);
        client.Dispose(); host.Dispose();
    }

    static void Full()
    {
        var host = new NetNode(Sock(0, 0, 0, 1), true, 2, Hs());
        var a = new NetNode(Sock(0, 0, 0, 2), false, 2, Hs());
        var b = new NetNode(Sock(0, 0, 0, 3), false, 2, Hs());
        bool aOk = false; string bReason = null;
        a.Connected += c => aOk = true;
        b.Rejected += r => bReason = r;
        var ep = new IPEndPoint(IPAddress.Loopback, Port(host.Socket));
        a.Connect(ep, Now());
        Run(() => aOk, 5, host, a);
        b.Connect(ep, Now());
        Run(() => bReason != null, 5, host, a, b);
        Check(aOk && bReason == "The game is full.", "third player rejected when MaxPlayers=2: " + bReason);
        a.Dispose(); b.Dispose(); host.Dispose();
    }

    static void FourPlayers()
    {
        var host = new NetNode(Sock(0, 0, 0, 1), true, 4, Hs());
        var clients = new List<NetNode>();
        int connected = 0;
        var ep = new IPEndPoint(IPAddress.Loopback, Port(host.Socket));
        for (int i = 0; i < 3; i++)
        {
            var c = new NetNode(Sock(0, 0, 0, 10 + i), false, 4, Hs());
            c.Connected += _ => connected++;
            c.Connect(ep, Now());
            clients.Add(c);
        }
        var all = new List<NetNode> { host }; all.AddRange(clients);
        Run(() => connected == 3, 5, all.ToArray());
        var slots = new HashSet<int>();
        foreach (var c in clients) slots.Add(c.LocalSlot);
        Check(connected == 3 && slots.SetEquals(new[] { 1, 2, 3 }), "4-player session: slots 1,2,3 assigned");
        foreach (var c in clients) c.Dispose();
        host.Dispose();
    }

    static void Timeout()
    {
        var host = new NetNode(Sock(0, 0, 0, 1), true, 2, Hs()) { TimeoutSeconds = 1.0 };
        var client = new NetNode(Sock(0, 0, 0, 2), false, 2, Hs());
        bool up = false; string why = null;
        client.Connected += _ => up = true;
        host.Disconnected += (c, r) => why = r;
        client.Connect(new IPEndPoint(IPAddress.Loopback, Port(host.Socket)), Now());
        Run(() => up, 5, host, client);
        Run(() => why != null, 4, host); // client stops talking
        Check(why == "timed out", "silent peer times out: " + why);
        client.Dispose(); host.Dispose();
    }

    static int Main()
    {
        Reliability(0, 0, 0);
        Reliability(80, 20, 5);
        Reliability(150, 40, 15);
        Mismatch();
        Full();
        FourPlayers();
        Timeout();
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
