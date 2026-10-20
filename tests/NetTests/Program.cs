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

    /// <summary>
    /// A long outage in the middle of a busy reliable stream: more packets go out than the ack ring remembers, so
    /// the first acks after the outage name packet numbers whose ring slots now hold newer packets. Afterwards the
    /// stream must catch up by itself: every message exactly once, in order, and nothing left pending.
    /// </summary>
    static void OutageRecovery()
    {
        bool dark = false;
        var host = new NetNode(Sock(0, 0, 0, 1), true, 2, Hs());
        var client = new NetNode(new OutageSocket(new UdpDatagramSocket(0), () => dark), false, 2, Hs());
        Connection toClient = null;
        host.Connected += c => toClient = c;
        var got = new List<int>();
        client.Message += (c, ch, data, d) => { if (d == Delivery.ReliableOrdered && ch == 1) got.Add(BitConverter.ToInt32(data, 0)); };
        client.Connect(new IPEndPoint(IPAddress.Loopback, Port(host.Socket)), Now());
        Run(() => toClient != null, 5, host, client);
        if (toClient == null) { Check(false, "outage test: connect"); return; }
        int sent = 0;
        double start = Now(), darkFrom = start + 2, darkTo = start + 10, stopSending = start + 13, end = start + 28;
        while (Now() < end)
        {
            double t = Now();
            dark = t >= darkFrom && t < darkTo;
            if (t < stopSending)
                for (int i = 0; i < 6; i++)
                {
                    var m = new byte[400];
                    BitConverter.GetBytes(sent++).CopyTo(m, 0);
                    toClient.Send(1, m, Delivery.ReliableOrdered);
                }
            toClient.Send(3, new byte[40], Delivery.Unreliable);
            host.Poll(t); client.Poll(t);
            host.Flush(t); client.Flush(t);
            if (t > stopSending && got.Count == sent && toClient.PendingReliable == 0) break;
            Thread.Sleep(3);
        }
        bool inOrder = got.Count == sent;
        for (int i = 0; i < got.Count && inOrder; i++) inOrder = got[i] == i;
        Check(inOrder && toClient.PendingReliable == 0 && host.Connections.Count == 1,
            $"reliable stream catches up after an 8 s outage ({got.Count}/{sent} delivered in order, {toClient.PendingReliable} pending, {toClient.PacketsSent} packets sent, caught up {Now() - darkTo:F1} s after)");
        client.Dispose(); host.Dispose();
    }

    /// <summary>
    /// Same as the game run that jammed: 70 ms link with 10 ms jitter and 1% loss, about 40 frames a second, the host
    /// sending about 9 reliable messages a frame plus a big fragmented one now and then, a 15 s outage on the guest.
    /// </summary>
    static void OutageRealistic(int seed)
    {
        bool dark = false;
        var host = new NetNode(new SimulatedLinkSocket(new UdpDatagramSocket(0), Now, 70, 10, 1, seed), true, 2, Hs());
        var client = new NetNode(new SimulatedLinkSocket(new OutageSocket(new UdpDatagramSocket(0), () => dark), Now, 70, 10, 1, seed + 1), false, 2, Hs());
        Connection toClient = null, toHost = null;
        host.Connected += c => toClient = c;
        client.Connected += c => toHost = c;
        var got = new List<int>();
        int bigGot = 0;
        client.Message += (c, ch, data, d) =>
        {
            if (d != Delivery.ReliableOrdered) return;
            if (ch == 1) got.Add(BitConverter.ToInt32(data, 0));
            if (ch == 2) bigGot++;
        };
        client.Connect(new IPEndPoint(IPAddress.Loopback, Port(host.Socket)), Now());
        Run(() => toClient != null && toHost != null, 8, host, client);
        if (toClient == null) { Check(false, "realistic outage test: connect"); return; }
        var rnd = new Random(seed);
        int sent = 0, bigSent = 0;
        double start = Now(), darkFrom = start + 3, darkTo = start + 18, stopSending = start + 24, end = start + 50;
        while (Now() < end)
        {
            double t = Now();
            dark = t >= darkFrom && t < darkTo;
            if (t < stopSending)
            {
                int n = 6 + rnd.Next(7);
                for (int i = 0; i < n; i++)
                {
                    var m = new byte[40 + rnd.Next(180)];
                    BitConverter.GetBytes(sent++).CopyTo(m, 0);
                    toClient.Send(1, m, Delivery.ReliableOrdered);
                }
                if (rnd.Next(40) == 0) { toClient.Send(2, new byte[3000 + rnd.Next(4000)], Delivery.ReliableOrdered); bigSent++; }
                if (rnd.Next(40) == 0) toHost.Send(1, new byte[30], Delivery.ReliableOrdered);
            }
            toClient.Send(3, new byte[300], Delivery.Unreliable);
            toHost.Send(3, new byte[60], Delivery.Unreliable);
            host.Poll(t); client.Poll(t);
            host.Flush(t); client.Flush(t);
            if (t > stopSending && got.Count == sent && toClient.PendingReliable == 0) break;
            Thread.Sleep(25);
        }
        bool inOrder = got.Count == sent;
        for (int i = 0; i < got.Count && inOrder; i++) inOrder = got[i] == i;
        Check(inOrder && bigGot == bigSent && toClient.PendingReliable == 0,
            $"[seed {seed}] stream catches up after a 15 s outage on a 70 ms link ({got.Count}/{sent} in order, big {bigGot}/{bigSent}, {toClient.PendingReliable} pending, {toClient.RelStats}, caught up {Now() - darkTo:F1} s after)");
        client.Dispose(); host.Dispose();
    }

    /// <summary>
    /// A 25 s loading screen on the guest: its game thread is frozen and only the keep-alive thread talks. The host keeps
    /// queueing reliable messages. The host must not drop the guest (not "timed out", not "stalled"), and once the
    /// guest is back every message arrives in order.
    /// </summary>
    static void LongLoadNoStall()
    {
        var host = new NetNode(Sock(0, 0, 0, 1), true, 2, Hs());
        var client = new NetNode(Sock(0, 0, 0, 2), false, 2, Hs());
        Connection toClient = null;
        string dropped = null;
        host.Connected += c => toClient = c;
        host.Disconnected += (c, why) => dropped = why;
        var got = new List<int>();
        client.Message += (c, ch, data, d) => { if (d == Delivery.ReliableOrdered && ch == 1) got.Add(BitConverter.ToInt32(data, 0)); };
        var hostEp = new IPEndPoint(IPAddress.Loopback, Port(host.Socket));
        client.Connect(hostEp, Now());
        Run(() => toClient != null, 5, host, client);
        if (toClient == null) { Check(false, "long load test: connect"); return; }
        int sent = 0;
        double start = Now(), loadEnd = start + 25, end = start + 40, nextKeepAlive = 0;
        var keepAlive = new byte[] { (byte)(Packet.Magic & 0xFF), (byte)(Packet.Magic >> 8), (byte)PacketType.KeepAlive };
        while (Now() < end)
        {
            double t = Now();
            bool loading = t < loadEnd;
            if (loading && t > start + 1)
            {
                if (t >= nextKeepAlive) { client.Socket.SendTo(hostEp, keepAlive, keepAlive.Length); nextKeepAlive = t + 0.5; }
            }
            else { client.Poll(t); client.Flush(t); }
            if (t < loadEnd + 2) for (int i = 0; i < 5; i++) toClient.Send(1, BitConverter.GetBytes(sent++), Delivery.ReliableOrdered);
            host.Poll(t); host.Flush(t);
            if (t > loadEnd + 2 && got.Count == sent && toClient.PendingReliable == 0) break;
            Thread.Sleep(16);
        }
        bool inOrder = got.Count == sent;
        for (int i = 0; i < got.Count && inOrder; i++) inOrder = got[i] == i;
        Check(dropped == null && inOrder, $"25 s loading screen with keep-alives only: not dropped ({dropped ?? "still connected"}), {got.Count}/{sent} delivered in order after");
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
        LongLoadNoStall();
        OutageRecovery();
        OutageRealistic(5);
        OutageRealistic(77);
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
