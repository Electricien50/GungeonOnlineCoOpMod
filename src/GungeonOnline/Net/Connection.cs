using System;
using System.Collections.Generic;
using System.Net;

namespace GungeonOnline.Net
{
    /// <summary>Delivery class of a message.</summary>
    public enum Delivery : byte
    {
        /// <summary>Fire and forget. Used for inputs (with app-level redundancy) and snapshots.</summary>
        Unreliable = 0,
        /// <summary>Delivered exactly once, in order. Used for events (spawns, deaths, doors, pickups).</summary>
        ReliableOrdered = 1,
    }

    /// <summary>
    /// One peer link: packet sequence numbers, acks (16-bit seq + 32-bit history), reliable ordered messages
    /// with resend and fragmentation, RTT and loss statistics. Transport-agnostic: it produces datagrams and
    /// consumes datagrams; the owner moves them over UDP or Steam.
    /// </summary>
    public sealed class Connection
    {
        public const int MaxDatagram = 1200;
        const int FragmentPayload = 1000;
        const int HeaderSize = 2 + 1 + 2 + 2 + 8 + 2; // magic, type, seq, ack, ackBits, cumulative reliable ack
        /// <summary>Datagram budget per flush per peer (about 9 KB per frame, 570 KB/s at 60 Hz).</summary>
        public int MaxPacketsPerFlush = 8;

        public readonly EndPoint Remote;
        public readonly int Slot;
        public object Tag;
        public string PlayerId = "";

        // Stats
        public float RttMs { get; private set; } = 100f;
        public float RttVarMs { get; private set; } = 50f;
        public float LossPercent { get; private set; }
        public long BytesSent, BytesReceived, PacketsSent, PacketsReceived, Resends;
        public double LastReceiveTime;

        // Packet sequencing
        ushort nextSeq;
        ushort remoteSeq;
        ulong remoteAckBits;
        bool anyReceived;
        readonly double[] sentTime = new double[1024];
        readonly List<ushort>[] packetMessages = new List<ushort>[1024];
        readonly bool[] packetAcked = new bool[1024];
        int lossWindowSent, lossWindowLost;

        // Reliable send
        ushort nextReliableId;
        sealed class Pending { public ushort Id; public byte Channel; public byte[] Data; public double LastSent; public bool Acked; public int Sends; }
        readonly LinkedList<Pending> pending = new LinkedList<Pending>();
        readonly Dictionary<ushort, LinkedListNode<Pending>> pendingById = new Dictionary<ushort, LinkedListNode<Pending>>();

        // Reliable receive
        ushort nextExpected;
        readonly Dictionary<ushort, KeyValuePair<byte, byte[]>> outOfOrder = new Dictionary<ushort, KeyValuePair<byte, byte[]>>();
        readonly List<byte> fragAssembly = new List<byte>();
        byte fragChannel;

        // Unreliable send queue for the next flush
        readonly List<KeyValuePair<byte, byte[]>> unreliableQueue = new List<KeyValuePair<byte, byte[]>>();

        readonly WireWriter w = new WireWriter(MaxDatagram + 64);

        public Connection(EndPoint remote, int slot) { Remote = remote; Slot = slot; }

        public float ResendTimeoutMs => Math.Max(30f, RttMs + 4f * RttVarMs);

        /// <summary>Queue a message. Large reliable messages are fragmented automatically.</summary>
        public void Send(byte channel, byte[] data, Delivery delivery)
        {
            if (delivery == Delivery.Unreliable)
            {
                if (data.Length > MaxDatagram - HeaderSize - 4) throw new ArgumentException("unreliable message too large");
                unreliableQueue.Add(new KeyValuePair<byte, byte[]>(channel, data));
                return;
            }
            if (data.Length <= FragmentPayload)
            {
                Enqueue(channel, data);
                return;
            }
            // Fragment: channel high bit 0x40 marks "more fragments follow".
            for (int off = 0; off < data.Length; off += FragmentPayload)
            {
                int n = Math.Min(FragmentPayload, data.Length - off);
                var part = new byte[n];
                Buffer.BlockCopy(data, off, part, 0, n);
                bool more = off + n < data.Length;
                Enqueue((byte)(channel | (more ? 0x40 : 0)), part);
            }
        }

        void Enqueue(byte channel, byte[] data)
        {
            var p = new Pending { Id = nextReliableId++, Channel = channel, Data = data, LastSent = -1 };
            pendingById[p.Id] = pending.AddLast(p);
        }

        public int PendingReliable => pending.Count;

        /// <summary>
        /// Build the datagrams to send now. Always emits at least one packet when <paramref name="forceAck"/>
        /// is set so acks flow even with nothing to say. Unacked reliable messages are resent after the timeout.
        /// </summary>
        public void Flush(double now, bool forceAck, Action<byte[], int> emit)
        {
            bool wroteAny = false;
            int ri = 0;
            var queuedRel = new List<Pending>();
            foreach (var p in pending)
                if (!p.Acked && (p.LastSent < 0 || (now - p.LastSent) * 1000.0 >= ResendTimeoutMs * Math.Min(8, 1 << Math.Max(0, p.Sends - 1))))
                    queuedRel.Add(p);

            int ui = 0;
            int packets = 0;
            while ((ui < unreliableQueue.Count || ri < queuedRel.Count || (forceAck && !wroteAny)) && packets < MaxPacketsPerFlush)
            {
                packets++;
                ushort seq = nextSeq++;
                BeginPacket(seq);
                var msgs = new List<ushort>();
                while (ri < queuedRel.Count)
                {
                    var p = queuedRel[ri];
                    int size = 1 + 2 + 3 + p.Data.Length;
                    if (w.Length + size > MaxDatagram && msgs.Count > 0) break;
                    w.U8((byte)(p.Channel | 0x80));
                    w.U16(p.Id);
                    w.VarU((uint)p.Data.Length);
                    w.Bytes(p.Data, 0, p.Data.Length);
                    if (p.LastSent >= 0) Resends++;
                    p.LastSent = now;
                    p.Sends++;
                    msgs.Add(p.Id);
                    ri++;
                }
                while (ui < unreliableQueue.Count)
                {
                    var m = unreliableQueue[ui];
                    int size = 1 + 3 + m.Value.Length;
                    if (w.Length + size > MaxDatagram && (msgs.Count > 0 || w.Length > HeaderSize)) break;
                    w.U8((byte)(m.Key & 0x3F));
                    w.VarU((uint)m.Value.Length);
                    w.Bytes(m.Value, 0, m.Value.Length);
                    ui++;
                }
                int slot = seq & 1023;
                sentTime[slot] = now;
                packetAcked[slot] = false;
                packetMessages[slot] = msgs;
                lossWindowSent++;
                PacketsSent++;
                BytesSent += w.Length;
                emit(w.Buffer, w.Length);
                wroteAny = true;
            }
            unreliableQueue.Clear();
        }

        void BeginPacket(ushort seq)
        {
            w.Reset();
            w.U16(Packet.Magic);
            w.U8((byte)PacketType.Data);
            w.U16(seq);
            w.U16(remoteSeq);
            w.U64(remoteAckBits);
            w.U16(nextExpected);
        }

        /// <summary>Process a Data datagram. Delivers messages in order through <paramref name="deliver"/>.</summary>
        public void Receive(byte[] buf, int offset, int count, double now, Action<byte, byte[], Delivery> deliver)
        {
            var r = new WireReader(buf, offset, count);
            if (r.U16() != Packet.Magic || r.U8() != (byte)PacketType.Data) return;
            ushort seq = r.U16();
            ushort ack = r.U16();
            ulong ackBits = r.U64();
            ushort relAck = r.U16();
            PacketsReceived++;
            BytesReceived += count;
            LastReceiveTime = now;

            // Track what we must ack back.
            if (!anyReceived) { remoteSeq = seq; remoteAckBits = 0; anyReceived = true; }
            else if (Seq.Greater(seq, remoteSeq))
            {
                int shift = Seq.Diff(seq, remoteSeq);
                remoteAckBits = shift > 64 ? 0 : ((shift == 64 ? 0 : remoteAckBits << shift) | (1ul << (shift - 1)));
                remoteSeq = seq;
            }
            else
            {
                int back = Seq.Diff(remoteSeq, seq);
                if (back >= 1 && back <= 64)
                {
                    ulong bit = 1ul << (back - 1);
                    if ((remoteAckBits & bit) != 0) return; // duplicate
                    remoteAckBits |= bit;
                }
                else return; // duplicate or ancient
            }

            // Process acks of our packets.
            Ack(ack, now);
            for (int i = 0; i < 64; i++)
                if ((ackBits & (1ul << i)) != 0) Ack((ushort)(ack - 1 - i), now);
            // Cumulative ack: every reliable id before relAck has been delivered.
            while (pending.First != null && Seq.Diff(relAck, pending.First.Value.Id) > 0)
            {
                pendingById.Remove(pending.First.Value.Id);
                pending.RemoveFirst();
            }

            while (r.Remaining > 0)
            {
                byte ch = r.U8();
                if ((ch & 0x80) != 0)
                {
                    ushort id = r.U16();
                    int len = (int)r.VarU();
                    var data = r.Bytes(len);
                    byte channel = (byte)(ch & 0x7F);
                    int d = Seq.Diff(id, nextExpected);
                    if (d < 0 || outOfOrder.ContainsKey(id)) continue; // already delivered or buffered
                    outOfOrder[id] = new KeyValuePair<byte, byte[]>(channel, data);
                    while (outOfOrder.TryGetValue(nextExpected, out var m))
                    {
                        outOfOrder.Remove(nextExpected);
                        nextExpected++;
                        DeliverReliable(m.Key, m.Value, deliver);
                    }
                }
                else
                {
                    int len = (int)r.VarU();
                    deliver((byte)(ch & 0x3F), r.Bytes(len), Delivery.Unreliable);
                }
            }
        }

        void DeliverReliable(byte channel, byte[] data, Action<byte, byte[], Delivery> deliver)
        {
            bool more = (channel & 0x40) != 0;
            byte ch = (byte)(channel & 0x3F);
            if (more || fragAssembly.Count > 0)
            {
                if (fragAssembly.Count == 0) fragChannel = ch;
                fragAssembly.AddRange(data);
                if (more) return;
                var whole = fragAssembly.ToArray();
                fragAssembly.Clear();
                deliver(fragChannel, whole, Delivery.ReliableOrdered);
                return;
            }
            deliver(ch, data, Delivery.ReliableOrdered);
        }

        void Ack(ushort seq, double now)
        {
            int slot = seq & 1023;
            if (packetAcked[slot] || packetMessages[slot] == null) return;
            double age = (now - sentTime[slot]) * 1000.0;
            if (age < 0 || age > 10000) return; // stale slot from a wrapped sequence
            packetAcked[slot] = true;
            float sample = (float)age;
            RttVarMs = 0.75f * RttVarMs + 0.25f * Math.Abs(sample - RttMs);
            RttMs = 0.875f * RttMs + 0.125f * sample;
            foreach (var id in packetMessages[slot])
                if (pendingById.TryGetValue(id, out var node))
                {
                    pending.Remove(node);
                    pendingById.Remove(id);
                }
            packetMessages[slot] = null;
        }

        /// <summary>Call once per second: recomputes loss from packets older than 2 RTT that were never acked.</summary>
        public void UpdateLoss(double now)
        {
            int lost = 0, total = 0;
            double horizon = Math.Max(0.2, 2 * RttMs / 1000.0);
            for (int i = 0; i < 1024; i++)
            {
                double age = now - sentTime[i];
                if (sentTime[i] <= 0 || age < horizon || age > horizon + 1.0) continue;
                total++;
                if (!packetAcked[i]) lost++;
            }
            LossPercent = total == 0 ? 0 : 100f * lost / total;
        }
    }

    public enum PacketType : byte
    {
        Connect = 1,
        Accept = 2,
        Reject = 3,
        Data = 4,
        Disconnect = 5,
        Punch = 6,
        KeepAlive = 7, // "still here": sent from a background thread while the game thread is busy loading
    }

    public static class Packet
    {
        public const ushort Magic = 0x4F47; // "GO"
    }
}
