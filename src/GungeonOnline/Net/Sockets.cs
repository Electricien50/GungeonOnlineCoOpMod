using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GungeonOnline.Net
{
    /// <summary>Datagram socket abstraction so the same session code runs over UDP, Steam, or a test link.</summary>
    public interface IDatagramSocket : IDisposable
    {
        void SendTo(EndPoint to, byte[] buf, int len);
        /// <summary>Non-blocking. Returns false when nothing is waiting.</summary>
        bool TryReceive(out EndPoint from, out byte[] data);
        EndPoint LocalEndPoint { get; }
        /// <summary>Seconds between the last returned datagram's real arrival (receive thread) and the TryReceive call; 0 if unknown.</summary>
        double LastArrivalAge { get; }
    }

    /// <summary>
    /// UDP socket with a background receive thread. Received datagrams wait in a locked queue and are
    /// drained by the game thread right before the simulation step, so arrival-to-use adds no frame.
    /// </summary>
    public sealed class UdpDatagramSocket : IDatagramSocket
    {
        readonly Socket socket;
        readonly Thread thread;
        struct Arrived { public EndPoint From; public byte[] Data; public long Ticks; }
        readonly Queue<Arrived> inbox = new Queue<Arrived>();
        volatile bool running = true;
        public Socket Raw => socket;
        public double LastArrivalAge { get; private set; }

        public UdpDatagramSocket(int port, bool ipv6 = false)
        {
            socket = new Socket(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Windows reports ICMP port-unreachable as a receive error on UDP sockets; turn that off.
            try { socket.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
            socket.ReceiveBufferSize = 1 << 20;
            socket.SendBufferSize = 1 << 20;
            socket.Bind(new IPEndPoint(ipv6 ? IPAddress.IPv6Any : IPAddress.Any, port));
            thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "GungeonOnline UDP" };
            thread.Start();
        }

        public EndPoint LocalEndPoint => socket.LocalEndPoint;

        void ReceiveLoop()
        {
            var buf = new byte[2048];
            while (running)
            {
                try
                {
                    // Wait at most 50 ms so the thread can always notice shutdown; returns at once when data is there.
                    if (!socket.Poll(50000, SelectMode.SelectRead)) continue;
                    EndPoint from = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    int n = socket.ReceiveFrom(buf, ref from);
                    if (n <= 0) continue;
                    var data = new byte[n];
                    Buffer.BlockCopy(buf, 0, data, 0, n);
                    lock (inbox)
                    {
                        if (inbox.Count < 8192) inbox.Enqueue(new Arrived { From = from, Data = data, Ticks = System.Diagnostics.Stopwatch.GetTimestamp() });
                    }
                }
                catch (SocketException) { if (!running) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        public void SendTo(EndPoint to, byte[] buf, int len)
        {
            try { socket.SendTo(buf, 0, len, SocketFlags.None, to); }
            catch (SocketException) { }
        }

        public bool TryReceive(out EndPoint from, out byte[] data)
        {
            lock (inbox)
            {
                if (inbox.Count == 0) { from = null; data = null; return false; }
                var a = inbox.Dequeue();
                from = a.From; data = a.Data;
                LastArrivalAge = (System.Diagnostics.Stopwatch.GetTimestamp() - a.Ticks) / (double)System.Diagnostics.Stopwatch.Frequency;
                return true;
            }
        }

        public void Dispose()
        {
            running = false;
            try { thread.Join(500); } catch { }
            try { socket.Close(); } catch { }
        }
    }

    /// <summary>
    /// Wraps a socket with simulated latency, jitter and loss on outgoing datagrams. With both ends wrapped,
    /// round trip = LatencyMs.
    /// Used by tests and by the GO_SIM_* harness switches; never on by default.
    /// </summary>
    public sealed class SimulatedLinkSocket : IDatagramSocket
    {
        readonly IDatagramSocket inner;
        readonly Func<double> clock;
        readonly Random rng;
        public float LatencyMs, JitterMs, LossPercent;

        struct Delayed { public double At; public EndPoint Ep; public byte[] Data; public bool Outgoing; }
        readonly List<Delayed> queue = new List<Delayed>();

        readonly System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
        readonly Thread pump;
        volatile bool running = true;

        public SimulatedLinkSocket(IDatagramSocket inner, Func<double> clock, float latencyMs, float jitterMs, float lossPercent, int seed = 1)
        {
            this.inner = inner; this.clock = clock; LatencyMs = latencyMs; JitterMs = jitterMs; LossPercent = lossPercent;
            rng = new Random(seed);
            // Delayed packets leave on time whatever the game thread is doing, like a real network would deliver them.
            pump = new Thread(() => { while (running) { Pump(); Thread.Sleep(1); } }) { IsBackground = true, Name = "GungeonOnline sim link" };
            pump.Start();
        }

        public EndPoint LocalEndPoint => inner.LocalEndPoint;
        public double LastArrivalAge => inner.LastArrivalAge;

        // Wall clock for the delay queue (the game clock is not readable off the main thread).
        double Wall => wall.Elapsed.TotalSeconds;

        double Delay() => (LatencyMs / 2 + (rng.NextDouble() * 2 - 1) * JitterMs / 2) / 1000.0;
        bool Drop() => rng.NextDouble() * 100 < LossPercent;

        public void SendTo(EndPoint to, byte[] buf, int len)
        {
            if (Drop()) return;
            var copy = new byte[len];
            Buffer.BlockCopy(buf, 0, copy, 0, len);
            lock (queue) queue.Add(new Delayed { At = Wall + Math.Max(0, Delay()), Ep = to, Data = copy, Outgoing = true });
        }

        void Pump()
        {
            double now = Wall;
            lock (queue)
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    var d = queue[i];
                    if (d.Outgoing && d.At <= now) { try { inner.SendTo(d.Ep, d.Data, d.Data.Length); } catch { } queue.RemoveAt(i--); }
                }
            }
        }

        public bool TryReceive(out EndPoint from, out byte[] data)
        {
            // Latency, jitter and loss are applied once, on the sending side; receive is a pass-through.
            return inner.TryReceive(out from, out data);
        }

        public void Dispose() { running = false; inner.Dispose(); }
    }
}

namespace GungeonOnline.Net
{
    /// <summary>
    /// Test-only: behaves like a strict home router or firewall. A datagram from an address this side has never
    /// sent to is dropped, so a friend's first join packet never arrives until this side has opened a path to them.
    /// </summary>
    public sealed class StrictRouterSocket : IDatagramSocket
    {
        readonly IDatagramSocket inner;
        readonly System.Collections.Generic.HashSet<string> opened = new System.Collections.Generic.HashSet<string>();
        public int Dropped;
        public StrictRouterSocket(IDatagramSocket inner) { this.inner = inner; }
        public System.Net.EndPoint LocalEndPoint => inner.LocalEndPoint;
        public double LastArrivalAge => inner.LastArrivalAge;
        public void SendTo(System.Net.EndPoint to, byte[] buf, int len)
        {
            lock (opened) opened.Add(to.ToString());
            inner.SendTo(to, buf, len);
        }
        public bool TryReceive(out System.Net.EndPoint from, out byte[] data)
        {
            while (inner.TryReceive(out from, out data))
            {
                bool ok;
                lock (opened) ok = opened.Contains(from.ToString());
                if (ok) return true;
                Dropped++;
            }
            return false;
        }
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Test-only: drops every datagram in and out while <see cref="Dropping"/> says so (simulated outage).</summary>
    public sealed class OutageSocket : IDatagramSocket
    {
        readonly IDatagramSocket inner;
        readonly System.Func<bool> dropping;
        public OutageSocket(IDatagramSocket inner, System.Func<bool> dropping) { this.inner = inner; this.dropping = dropping; }
        public System.Net.EndPoint LocalEndPoint => inner.LocalEndPoint;
        public double LastArrivalAge => inner.LastArrivalAge;
        public void SendTo(System.Net.EndPoint to, byte[] buf, int len) { if (!dropping()) inner.SendTo(to, buf, len); }
        public bool TryReceive(out System.Net.EndPoint from, out byte[] data)
        {
            while (inner.TryReceive(out from, out data)) if (!dropping()) return true;
            return false;
        }
        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// Several sockets behind one: receives from all, sends through whichever owns the address type.
    /// Lets one host accept Steam friends (Steam P2P) and GOG/Epic players (UDP) in the same session.
    /// </summary>
    public sealed class CompositeSocket : IDatagramSocket
    {
        readonly IDatagramSocket[] parts;
        readonly System.Func<System.Net.EndPoint, int> route;

        public CompositeSocket(System.Func<System.Net.EndPoint, int> route, params IDatagramSocket[] parts)
        {
            this.parts = parts; this.route = route;
        }

        public System.Net.EndPoint LocalEndPoint => parts[0].LocalEndPoint;
        public double LastArrivalAge { get; private set; }

        public void SendTo(System.Net.EndPoint to, byte[] buf, int len) => parts[route(to)].SendTo(to, buf, len);

        public bool TryReceive(out System.Net.EndPoint from, out byte[] data)
        {
            foreach (var p in parts)
                if (p.TryReceive(out from, out data)) { LastArrivalAge = p.LastArrivalAge; return true; }
            from = null; data = null;
            return false;
        }

        public void Dispose()
        {
            foreach (var p in parts) p.Dispose();
        }
    }
}
