using System;
using System.Collections.Generic;
using System.Net;

namespace GungeonOnline.Net
{
    /// <summary>What a joining peer presents. Host rejects on any mismatch.</summary>
    public sealed class Handshake
    {
        public const ushort ProtocolVersion = 3;
        public string ModVersion = "";
        public string GameHash = "";
        public string PlayerName = "";
        /// <summary>Permanent per-install id, so a returning player gets their own slot back.</summary>
        public string PlayerId = "";
    }

    /// <summary>
    /// Star topology node. The host accepts up to MaxPlayers-1 peers (slot 0 is the host itself);
    /// a client holds one connection to the host. Handles connect/accept/reject, timeouts and disconnects,
    /// and hands messages to the session layer. No Unity dependency.
    /// </summary>
    public sealed class NetNode : IDisposable
    {
        public readonly bool IsHost;
        public readonly int MaxPlayers;
        public readonly Handshake Local;
        public IDatagramSocket Socket { get; private set; }
        public readonly List<Connection> Connections = new List<Connection>();
        public double TimeoutSeconds = 20.0;
        /// <summary>Host: pick a slot for a joining player id (returning players get theirs back). -1 = next free.</summary>
        public Func<string, int> SlotForPlayer;

        public event Action<Connection> Connected;
        public event Action<Connection, string> Disconnected;
        public event Action<Connection, byte, byte[], Delivery> Message;
        public event Action<string> Rejected;
        /// <summary>Host: someone tried to join (their address, their mod version, and why they were turned away, or null).</summary>
        public event Action<EndPoint, string, string> JoinAttempt;
        uint lastAttemptNonce;
        /// <summary>Datagram that is not part of any connection (STUN replies, punches). Return true if consumed.</summary>
        public Func<EndPoint, byte[], bool> RawHandler;

        // Client-side connect state
        EndPoint hostEp;
        public EndPoint LastHost => hostEp;
        uint nonce;
        double nextConnectSend;
        double connectDeadline;
        public bool Connecting { get; private set; }
        public int LocalSlot { get; private set; } = -1;

        readonly Dictionary<string, uint> acceptedNonces = new Dictionary<string, uint>();
        readonly WireWriter w = new WireWriter(512);
        readonly Random rng = new Random();

        public NetNode(IDatagramSocket socket, bool isHost, int maxPlayers, Handshake local)
        {
            Socket = socket; IsHost = isHost; MaxPlayers = maxPlayers; Local = local;
            if (isHost) LocalSlot = 0;
        }

        static string Key(EndPoint ep) => ep.ToString();

        public Connection Find(EndPoint ep)
        {
            string k = Key(ep);
            foreach (var c in Connections) if (Key(c.Remote) == k) return c;
            return null;
        }

        public void Connect(EndPoint host, double now, double timeoutSeconds = 15)
        {
            hostEp = host;
            nonce = (uint)rng.Next() | 1u;
            Connecting = true;
            nextConnectSend = now;
            connectDeadline = now + timeoutSeconds;
        }

        /// <summary>Drain the socket and process everything that arrived. Call right before the sim step.</summary>
        double lastPoll = -1;

        public void Poll(double now)
        {
            // Time this game spent frozen (a long load) is not the other side's silence: do not count it.
            if (lastPoll >= 0 && now - lastPoll > 2.0)
                foreach (var c in Connections) c.LastReceiveTime += now - lastPoll;
            lastPoll = now;
            while (Socket.TryReceive(out var from, out var data))
            {
                try { Handle(from, data, now); }
                catch (WireException) { /* malformed datagram: drop */ }
            }

            if (Connecting)
            {
                if (now >= connectDeadline) { Connecting = false; Rejected?.Invoke("no answer from host"); }
                else if (now >= nextConnectSend) { SendConnect(); nextConnectSend = now + 0.2; }
            }

            for (int i = Connections.Count - 1; i >= 0; i--)
            {
                var c = Connections[i];
                if (now - c.LastReceiveTime > TimeoutSeconds) Drop(c, "timed out", sendNotice: false);
            }
        }

        void Handle(EndPoint from, byte[] data, double now)
        {
            if (data.Length < 3 || (data[0] | data[1] << 8) != Packet.Magic)
            {
                RawHandler?.Invoke(from, data);
                return;
            }
            var type = (PacketType)data[2];
            var r = new WireReader(data, 3, data.Length - 3);
            switch (type)
            {
                case PacketType.Data:
                    {
                        var c = Find(from);
                        if (c == null) return;
                        c.Receive(data, 0, data.Length, now, (ch, payload, del) => Message?.Invoke(c, ch, payload, del));
                        break;
                    }
                case PacketType.Connect:
                    if (IsHost) HandleConnect(from, ref r, now);
                    break;
                case PacketType.KeepAlive:
                    {
                        var c = Find(from);
                        if (c != null) c.LastReceiveTime = now;
                        break;
                    }
                case PacketType.Accept:
                    if (!IsHost && Connecting && r.U32() == nonce)
                    {
                        LocalSlot = r.U8();
                        r.U8(); // host max players
                        Connecting = false;
                        var c = new Connection(from, 0) { LastReceiveTime = now, Session = nonce };
                        Connections.Add(c);
                        Connected?.Invoke(c);
                    }
                    break;
                case PacketType.Reject:
                    if (!IsHost && Connecting && r.U32() == nonce)
                    {
                        Connecting = false;
                        Rejected?.Invoke(r.Str());
                    }
                    break;
                case PacketType.Disconnect:
                    {
                        var c = Find(from);
                        if (c != null) Drop(c, r.Remaining > 0 ? r.Str() : "left", sendNotice: false);
                        break;
                    }
                case PacketType.Punch:
                    RawHandler?.Invoke(from, data);
                    break;
            }
        }

        void HandleConnect(EndPoint from, ref WireReader r, double now)
        {
            uint n = r.U32();
            ushort proto = r.U16();
            string mod = r.Str(), hash = r.Str(), name = r.Str();
            string pid = r.Remaining > 0 ? r.Str() : "";
            var existing = Find(from);
            if (existing != null)
            {
                if (acceptedNonces.TryGetValue(Key(from), out var prev) && prev == n) { SendAccept(from, n, existing.Slot); return; }
                Drop(existing, "reconnected", sendNotice: false); // same address, new session: replace
            }
            string reason = null;
            if (proto != Handshake.ProtocolVersion) reason = $"Your friend has a different version of the mod. Both of you need the same version.";
            else if (mod != Local.ModVersion) reason = $"Different mod versions (host {Local.ModVersion}, you {mod}). Both of you need the same version.";
            else if (hash != Local.GameHash) reason = "Your game version is different from the host's. Update the game on both PCs.";
            int slot = -1;
            if (reason == null)
            {
                int wanted = SlotForPlayer != null ? SlotForPlayer(pid) : -1;
                slot = wanted > 0 && !SlotTaken(wanted) ? wanted : FreeSlot();
            }
            if (reason == null && slot < 0) reason = "The game is full.";
            if (n != lastAttemptNonce) { lastAttemptNonce = n; JoinAttempt?.Invoke(from, mod, reason); }
            if (reason != null)
            {
                w.Reset(); w.U16(Packet.Magic); w.U8((byte)PacketType.Reject); w.U32(n); w.Str(reason);
                Socket.SendTo(from, w.Buffer, w.Length);
                return;
            }
            var c = new Connection(from, slot) { LastReceiveTime = now, Tag = name, PlayerId = pid, Session = n };
            Connections.Add(c);
            acceptedNonces[Key(from)] = n;
            SendAccept(from, n, slot);
            Connected?.Invoke(c);
        }

        bool SlotTaken(int s)
        {
            foreach (var c in Connections) if (c.Slot == s) return true;
            return false;
        }

        int FreeSlot()
        {
            for (int s = 1; s < MaxPlayers; s++)
            {
                bool used = false;
                foreach (var c in Connections) if (c.Slot == s) used = true;
                if (!used) return s;
            }
            return -1;
        }

        void SendAccept(EndPoint to, uint n, int slot)
        {
            w.Reset(); w.U16(Packet.Magic); w.U8((byte)PacketType.Accept); w.U32(n); w.U8((byte)slot); w.U8((byte)MaxPlayers);
            Socket.SendTo(to, w.Buffer, w.Length);
        }

        void SendConnect()
        {
            w.Reset(); w.U16(Packet.Magic); w.U8((byte)PacketType.Connect); w.U32(nonce); w.U16(Handshake.ProtocolVersion);
            w.Str(Local.ModVersion); w.Str(Local.GameHash); w.Str(Local.PlayerName); w.Str(Local.PlayerId);
            Socket.SendTo(hostEp, w.Buffer, w.Length);
        }

        public void Send(Connection c, byte channel, byte[] data, Delivery d) => c.Send(channel, data, d);

        public void Broadcast(byte channel, byte[] data, Delivery d)
        {
            foreach (var c in Connections) c.Send(channel, data, d);
        }

        /// <summary>Send everything queued this frame. Call once per frame after the sim step.</summary>
        public void Flush(double now)
        {
            foreach (var c in Connections)
                c.Flush(now, forceAck: true, emit: (buf, len) => Socket.SendTo(c.Remote, buf, len));
        }

        public void UpdateStats(double now)
        {
            foreach (var c in Connections) c.UpdateLoss(now);
        }

        public void Drop(Connection c, string reason, bool sendNotice = true)
        {
            if (sendNotice)
            {
                w.Reset(); w.U16(Packet.Magic); w.U8((byte)PacketType.Disconnect); w.Str(reason ?? "");
                for (int i = 0; i < 3; i++) Socket.SendTo(c.Remote, w.Buffer, w.Length);
            }
            Connections.Remove(c);
            acceptedNonces.Remove(Key(c.Remote));
            Disconnected?.Invoke(c, reason);
        }

        public void Dispose()
        {
            foreach (var c in Connections.ToArray()) Drop(c, "session closed");
            Socket.Dispose();
        }
    }
}
