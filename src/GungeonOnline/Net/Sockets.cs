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
    }

    /// <summary>
    /// UDP socket with a background receive thread. Received datagrams wait in a locked queue and are
    /// drained by the game thread right before the simulation step, so arrival-to-use adds no frame.
    /// </summary>
    public sealed class UdpDatagramSocket : IDatagramSocket
    {
        readonly Socket socket;
        readonly Thread thread;
        readonly Queue<KeyValuePair<EndPoint, byte[]>> inbox = new Queue<KeyValuePair<EndPoint, byte[]>>();
        volatile bool running = true;
        public Socket Raw => socket;

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
                    EndPoint from = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    int n = socket.ReceiveFrom(buf, ref from);
                    if (n <= 0) continue;
                    var data = new byte[n];
                    Buffer.BlockCopy(buf, 0, data, 0, n);
                    lock (inbox)
                    {
                        if (inbox.Count < 8192) inbox.Enqueue(new KeyValuePair<EndPoint, byte[]>(from, data));
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
                var kv = inbox.Dequeue();
                from = kv.Key; data = kv.Value;
                return true;
            }
        }

        public void Dispose()
        {
            running = false;
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

        public SimulatedLinkSocket(IDatagramSocket inner, Func<double> clock, float latencyMs, float jitterMs, float lossPercent, int seed = 1)
        {
            this.inner = inner; this.clock = clock; LatencyMs = latencyMs; JitterMs = jitterMs; LossPercent = lossPercent;
            rng = new Random(seed);
        }

        public EndPoint LocalEndPoint => inner.LocalEndPoint;

        double Delay() => (LatencyMs / 2 + (rng.NextDouble() * 2 - 1) * JitterMs / 2) / 1000.0;
        bool Drop() => rng.NextDouble() * 100 < LossPercent;

        public void SendTo(EndPoint to, byte[] buf, int len)
        {
            if (Drop()) return;
            var copy = new byte[len];
            Buffer.BlockCopy(buf, 0, copy, 0, len);
            queue.Add(new Delayed { At = clock() + Math.Max(0, Delay()), Ep = to, Data = copy, Outgoing = true });
            Pump();
        }

        void Pump()
        {
            double now = clock();
            for (int i = 0; i < queue.Count; i++)
            {
                var d = queue[i];
                if (d.Outgoing && d.At <= now) { inner.SendTo(d.Ep, d.Data, d.Data.Length); queue.RemoveAt(i--); }
            }
        }

        public bool TryReceive(out EndPoint from, out byte[] data)
        {
            Pump();
            // Latency, jitter and loss are applied once, on the sending side; receive is a pass-through.
            return inner.TryReceive(out from, out data);
        }

        public void Dispose() => inner.Dispose();
    }
}
