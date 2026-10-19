using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using GungeonOnline.Net;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>Message channels. Values are on the wire; never reorder.</summary>
    public enum Channel : byte
    {
        Control = 0,
        Input = 1,
        Snapshot = 2,
        Event = 3,
        Lobby = 4,
    }

    /// <summary>
    /// The live online session. Receives at the very start of each frame (before input is polled and before
    /// any gameplay update) and sends at the very end (after physics), so a packet never waits an extra frame.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class NetSession : MonoBehaviour
    {
        public static NetSession Instance { get; private set; }
        public static bool Active => Instance != null && Instance.node != null;
        public static bool IsHost => Active && Instance.node.IsHost;
        public static int LocalSlot => Active ? Instance.node.LocalSlot : 0;

        NetNode node;
        StunClient stun;
        SteamLobby steam;
        bool steamInitTried;
        System.Net.EndPoint steamHost;
        UpnpMapper upnp;
        public string Status = "Offline";
        public string InviteCodeText;
        public string LastError;
        readonly List<KeyValuePair<IPEndPoint, double>> punchTargets = new List<KeyValuePair<IPEndPoint, double>>();
        double nextStats;

        public NetNode Node => node;
        public StunClient Stun => stun;
        public UpnpMapper Upnp => upnp;
        public bool SteamReady => steam != null;
        public void InviteSteamFriend() => steam?.Invite();

        readonly Dictionary<Channel, Action<Connection, byte[], Delivery>> handlers = new Dictionary<Channel, Action<Connection, byte[], Delivery>>();
        public event Action<Connection> PeerJoined;
        public event Action<Connection, string> PeerLeft;
        public event Action SessionEnded;

        static string gameHash;
        public static string GameHash
        {
            get
            {
                if (gameHash != null) return gameHash;
                try
                {
                    var path = Path.Combine(Path.Combine(Application.dataPath, "Managed"), "Assembly-CSharp.dll");
                    using (var md5 = MD5.Create())
                    using (var f = File.OpenRead(path))
                        gameHash = BitConverter.ToString(md5.ComputeHash(f)).Replace("-", "");
                }
                catch (Exception e) { gameHash = "unknown"; Plugin.Log.LogWarning("Game hash: " + e.Message); }
                return gameHash;
            }
        }

        public static double Now => Time.realtimeSinceStartup;

        void Awake()
        {
            Instance = this;
            Input.InputPump.BeforePoll += EarlyPoll;
            gameObject.AddComponent<NetFlush>();
        }

        public void On(Channel ch, Action<Connection, byte[], Delivery> h) => handlers[ch] = h;

        Handshake MakeHandshake() => new Handshake { ModVersion = Plugin.Version, GameHash = GameHash, PlayerName = NetConfig.PlayerName.Value };

        IDatagramSocket MakeSocket(int port, bool withSteam)
        {
            IDatagramSocket s = new UdpDatagramSocket(port);
            if (withSteam && steam != null)
            {
                var steamSock = new SteamDatagramSocket(id => node != null && (node.IsHost || (steamHost is SteamEndPoint h && h.Id == id)));
                s = new CompositeSocket(ep => ep is SteamEndPoint ? 1 : 0, s, steamSock);
            }
            float lat = EnvF("GO_SIM_LAT"), jit = EnvF("GO_SIM_JIT"), loss = EnvF("GO_SIM_LOSS");
            if (lat > 0 || jit > 0 || loss > 0)
            {
                Plugin.Log.LogInfo($"Simulated link: rtt={lat}ms jitter={jit}ms loss={loss}%");
                s = new SimulatedLinkSocket(s, () => Now, lat, jit, loss, Environment.TickCount);
            }
            return s;
        }

        static float EnvF(string k)
        {
            var v = Environment.GetEnvironmentVariable(k);
            float f;
            return v != null && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) ? f : 0f;
        }

        public void Host()
        {
            if (Active) return;
            try
            {
                int port = Dev.DevEnv.PortOverride ?? NetConfig.Port.Value;
                node = new NetNode(MakeSocket(port, withSteam: true), true, NetConfig.MaxPlayers.Value, MakeHandshake());
                steam?.Host(NetConfig.MaxPlayers.Value);
                Wire();
                StartStun();
                if (NetConfig.UseUpnp.Value && !Dev.DevEnv.IsHarness)
                {
                    upnp = new UpnpMapper();
                    upnp.MapAsync(port, "Gungeon Online");
                }
                Status = "Hosting";
                Game.BackgroundRun.SessionActive = true;
                Game.BackgroundRun.Refresh();
                Plugin.Log.LogInfo($"Hosting on UDP {port}, max {NetConfig.MaxPlayers.Value} players");
            }
            catch (Exception e) { Fail("Could not host: " + e.Message); }
        }

        public void Join(string code)
        {
            if (Active) return;
            if (!InviteCode.TryParse(code, out var ep)) { LastError = "That code is not valid."; return; }
            try
            {
                node = new NetNode(MakeSocket(Dev.DevEnv.PortOverride ?? 0, withSteam: false), false, NetConfig.MaxPlayers.Value, MakeHandshake());
                Wire();
                StartStun();
                node.Connect(ep, Now);
                // Opening our NAT toward the host lets a host behind a strict NAT reach us once it punches back.
                AddPunch(ep);
                Status = "Joining " + ep;
                Game.BackgroundRun.SessionActive = true;
                Game.BackgroundRun.Refresh();
                Plugin.Log.LogInfo("Joining " + ep);
            }
            catch (Exception e) { Fail("Could not join: " + e.Message); }
        }

        /// <summary>Host side: punch toward a joiner's reply code when routers block the first contact.</summary>
        public void LetIn(string code)
        {
            if (!Active || !node.IsHost) return;
            if (!InviteCode.TryParse(code, out var ep)) { LastError = "That code is not valid."; return; }
            AddPunch(ep);
            Plugin.Log.LogInfo("Punching toward " + ep);
        }

        /// <summary>Accepted a Steam invite: connect to the lobby owner over Steam P2P.</summary>
        void JoinSteam(Steamworks.CSteamID owner)
        {
            if (Active) Leave("joining another session");
            try
            {
                steamHost = new SteamEndPoint(owner);
                node = new NetNode(MakeSocket(Dev.DevEnv.PortOverride ?? 0, withSteam: true), false, NetConfig.MaxPlayers.Value, MakeHandshake());
                Wire();
                node.Connect(steamHost, Now);
                Status = "Joining Steam friend";
                Game.BackgroundRun.SessionActive = true;
                Game.BackgroundRun.Refresh();
            }
            catch (Exception e) { Fail("Could not join over Steam: " + e.Message); }
        }

        void AddPunch(IPEndPoint ep) => punchTargets.Add(new KeyValuePair<IPEndPoint, double>(ep, Now + 8.0));

        void StartStun()
        {
            var parts = NetConfig.StunServer.Value.Split(':');
            int port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 19302;
            stun = new StunClient(node.Socket, parts[0], port);
        }

        void Wire()
        {
            node.RawHandler = (from, data) => stun != null && stun.TryHandle(data);
            node.Connected += c =>
            {
                Plugin.Log.LogInfo($"Peer connected: slot {c.Slot} {c.Remote} name={c.Tag}");
                if (!node.IsHost) Status = "Connected (slot " + node.LocalSlot + ")";
                PeerJoined?.Invoke(c);
            };
            node.Disconnected += (c, why) =>
            {
                Plugin.Log.LogInfo($"Peer left: slot {c.Slot} ({why})");
                PeerLeft?.Invoke(c, why);
                if (!node.IsHost) Leave("Host connection lost: " + why);
            };
            node.Rejected += why => { Plugin.Log.LogWarning("Join rejected: " + why); Leave("Could not join: " + why); };
            node.Message += (c, ch, data, d) =>
            {
                if (handlers.TryGetValue((Channel)ch, out var h))
                {
                    try { h(c, data, d); }
                    catch (Exception e) { Plugin.Log.LogError($"Handler {(Channel)ch}: {e}"); }
                }
            };
        }

        void EarlyPoll() => PollNow();

        /// <summary>Drain the network now. Safe to call several times per frame.</summary>
        public void PollNow()
        {
            if (node != null) node.Poll(Now);
        }

        void Update()
        {
            if (!steamInitTried && Time.frameCount > 30)
            {
                steamInitTried = true;
                if (SteamLobby.Available)
                {
                    steam = new SteamLobby();
                    steam.Init();
                    steam.JoinHost += JoinSteam;
                    if (NetConfig.PlayerName.Value == "Gungeoneer")
                        try { NetConfig.PlayerName.Value = Steamworks.SteamFriends.GetPersonaName(); } catch { }
                    Plugin.Log.LogInfo("Steam available: invites enabled");
                }
            }
            if (node == null) return;
            double now = Now;
            node.Poll(now);
            stun?.Update(now);
            if (stun != null && stun.Result != null && InviteCodeText == null)
            {
                // Hosts behind UPnP keep the same port outside; otherwise the STUN-mapped port is the one to share.
                InviteCodeText = InviteCode.Encode(stun.Result);
                Plugin.Log.LogInfo("Invite code ready: " + InviteCodeText);
            }
            for (int i = punchTargets.Count - 1; i >= 0; i--)
            {
                if (now > punchTargets[i].Value) { punchTargets.RemoveAt(i); continue; }
                var b = new byte[] { (byte)(Packet.Magic & 0xFF), (byte)(Packet.Magic >> 8), (byte)PacketType.Punch };
                node.Socket.SendTo(punchTargets[i].Key, b, b.Length);
            }
            if (now >= nextStats) { node.UpdateStats(now); nextStats = now + 1.0; }
        }

        /// <summary>Called by <see cref="NetFlush"/> at the very end of the frame.</summary>
        internal void FlushNow()
        {
            if (node != null) node.Flush(Now);
        }

        public void Leave(string reason = null)
        {
            if (node == null) return;
            var n = node;
            node = null;
            try { n.Dispose(); } catch { }
            upnp?.Unmap();
            steam?.Leave();
            steamHost = null;
            upnp = null;
            stun = null;
            InviteCodeText = null;
            punchTargets.Clear();
            Status = "Offline";
            if (reason != null) LastError = reason;
            Game.BackgroundRun.SessionActive = false;
            Plugin.Log.LogInfo("Session ended" + (reason != null ? ": " + reason : ""));
            SessionEnded?.Invoke();
        }

        void Fail(string msg)
        {
            Plugin.Log.LogError(msg);
            LastError = msg;
            Leave();
        }

        void OnApplicationQuit() => Leave("game closed");
    }

    /// <summary>Sends everything queued this frame after all gameplay and physics have run.</summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class NetFlush : MonoBehaviour
    {
        void LateUpdate() => NetSession.Instance?.FlushNow();
    }
}
