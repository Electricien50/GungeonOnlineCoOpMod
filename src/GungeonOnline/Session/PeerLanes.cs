using System.Collections.Generic;
using System.Net;
using GungeonOnline.Net;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Direct lanes between guests. With three or four players a guest's input used to reach the other guests
    /// through the host (two hops). The host tells every guest where the others are; each guest then opens a
    /// lane straight to each other guest and sends its input down it as well as to the host. Whichever copy
    /// arrives first is used (input frames are numbered), so the host relay stays as the fallback and nothing
    /// changes about who runs the world. Only input travels on lanes.
    /// </summary>
    internal sealed class PeerLanes : MonoBehaviour
    {
        const byte MsgPeers = 5; // Lobby channel, host to guests
        const byte KindHello = 0, KindInput = 1;

        public static PeerLanes Instance { get; private set; }
        /// <summary>Steam ids of fellow guests, so their packets are let through the Steam socket.</summary>
        public static readonly HashSet<ulong> KnownSteamIds = new HashSet<ulong>();

        sealed class Lane
        {
            public int Slot;
            public EndPoint Remote;
            public bool Open;
            public double LastHeard = -1, NextHello;
            public int SentInputs, Hellos;
        }

        readonly Dictionary<int, Lane> lanes = new Dictionary<int, Lane>();
        uint token;         // the host's random session token, carried by every lane packet
        double nextPeersSend;
        readonly WireWriter w = new WireWriter(512);
        NetNode wired;

        void Awake() { Instance = this; }

        void Start()
        {
            var s = NetSession.Instance;
            s.On(Channel.Lobby, OnLobby);
            s.PeerJoined += c => { if (NetSession.IsHost) nextPeersSend = 0; };
            s.PeerLeft += (c, why) => { if (NetSession.IsHost) nextPeersSend = 0; };
            s.SessionEnded += () => { lanes.Clear(); KnownSteamIds.Clear(); token = 0; wired = null; };
        }

        static double Now => Net.Clock.Now;

        void Update()
        {
            var s = NetSession.Instance;
            if (s == null || !NetSession.Active || s.Node == null) return;
            if (wired != s.Node) { wired = s.Node; wired.LaneHandler = OnLane; }
            if (NetSession.IsHost)
            {
                if (token == 0) token = (uint)Random.Range(1, int.MaxValue);
                if (Now >= nextPeersSend && s.Node.Connections.Count > 1) { SendPeers(); nextPeersSend = Now + 3.0; }
                return;
            }
            foreach (var lane in lanes.Values)
            {
                if (lane.Open && lane.LastHeard >= 0 && Now - lane.LastHeard > 6.0)
                {
                    lane.Open = false;
                    Plugin.Log.LogInfo($"Lane to slot {lane.Slot} went quiet, back to the host relay");
                }
                if (Now >= lane.NextHello)
                {
                    SendLane(lane, KindHello, null, 0, 0);
                    lane.Hellos++;
                    lane.NextHello = Now + (lane.Open ? 1.0 : 0.25);
                }
            }
        }

        // ---------- Host: who is where ----------

        void SendPeers()
        {
            var node = NetSession.Instance.Node;
            w.Reset(); w.U8(MsgPeers); w.U32(token); w.U8((byte)node.Connections.Count);
            foreach (var c in node.Connections) { w.U8((byte)c.Slot); w.Str(c.Remote.ToString()); }
            node.Broadcast((byte)Channel.Lobby, w.ToArray(), Delivery.ReliableOrdered);
        }

        void OnLobby(Connection c, byte[] data, Delivery d)
        {
            if (data.Length == 0 || data[0] != MsgPeers || NetSession.IsHost) return;
            var r = new WireReader(data, 1, data.Length - 1);
            token = r.U32();
            int n = r.U8();
            var seen = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                int slot = r.U8();
                string where = r.Str();
                if (slot == NetSession.LocalSlot) continue;
                var ep = Parse(where);
                if (ep == null) continue;
                seen.Add(slot);
                if (ep is SteamEndPoint se) KnownSteamIds.Add(se.Id.m_SteamID);
                if (!lanes.TryGetValue(slot, out var lane) || !lane.Remote.Equals(ep))
                    lanes[slot] = new Lane { Slot = slot, Remote = ep };
            }
            foreach (var slot in new List<int>(lanes.Keys)) if (!seen.Contains(slot)) lanes.Remove(slot);
        }

        static EndPoint Parse(string where)
        {
            if (string.IsNullOrEmpty(where)) return null;
            if (where.StartsWith("steam:")) return ulong.TryParse(where.Substring(6), out var id) ? new SteamEndPoint(new Steamworks.CSteamID(id)) : null;
            int colon = where.LastIndexOf(':');
            if (colon <= 0) return null;
            string host = where.Substring(0, colon).Trim('[', ']');
            return IPAddress.TryParse(host, out var ip) && int.TryParse(where.Substring(colon + 1), out var port) ? new IPEndPoint(ip, port) : null;
        }

        // ---------- Guests: the lanes ----------

        void SendLane(Lane lane, byte kind, byte[] payload, int offset, int count)
        {
            var node = NetSession.Instance.Node;
            if (node == null || token == 0) return;
            w.Reset(); w.U16(Packet.Magic); w.U8((byte)PacketType.Lane); w.U32(token); w.U8((byte)NetSession.LocalSlot); w.U8(kind);
            if (payload != null) w.Bytes(payload, offset, count);
            node.Socket.SendTo(lane.Remote, w.Buffer, w.Length);
        }

        /// <summary>Our input for this frame, straight to every open lane (the host gets it through its connection).</summary>
        public void SendInput(byte[] payload)
        {
            if (NetSession.IsHost) return;
            foreach (var lane in lanes.Values)
                if (lane.Open) { SendLane(lane, KindInput, payload, 0, payload.Length); lane.SentInputs++; }
        }

        void OnLane(EndPoint from, byte[] data, int pos)
        {
            if (NetSession.IsHost || token == 0) return;
            var r = new WireReader(data, pos, data.Length - pos);
            if (r.U32() != token) return;
            int slot = r.U8();
            byte kind = r.U8();
            // Only the address the host told us for that slot may speak for it.
            if (!lanes.TryGetValue(slot, out var lane) || !lane.Remote.Equals(from)) return;
            lane.LastHeard = Now;
            if (!lane.Open)
            {
                lane.Open = true;
                Plugin.Log.LogInfo($"Lane open: slot {slot} direct ({from})");
                SendLane(lane, KindHello, null, 0, 0); // answer at once so their side opens too
            }
            if (kind == KindInput) Replication.InputSync.Instance?.IngestLane(slot, data, pos + 4 + 1 + 1);
        }

        /// <summary>For the status line and the menu: "direct" or "via host" for a slot.</summary>
        public static bool IsDirect(int slot) => Instance != null && Instance.lanes.TryGetValue(slot, out var l) && l.Open;
    }
}
