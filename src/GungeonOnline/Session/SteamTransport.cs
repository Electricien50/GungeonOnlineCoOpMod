using System;
using System.Net;
using System.Net.Sockets;
using GungeonOnline.Net;
using Steamworks;

namespace GungeonOnline.Session
{
    /// <summary>A Steam user as a network address.</summary>
    public sealed class SteamEndPoint : EndPoint
    {
        public readonly CSteamID Id;
        public SteamEndPoint(CSteamID id) { Id = id; }
        public override AddressFamily AddressFamily => AddressFamily.Unspecified;
        public override string ToString() => "steam:" + Id.m_SteamID;
        public override bool Equals(object o) => o is SteamEndPoint s && s.Id == Id;
        public override int GetHashCode() => Id.GetHashCode();
    }

    /// <summary>
    /// Datagrams over Steam's peer-to-peer networking (NAT traversal and Steam relays, no ports to open).
    /// Same session code as UDP: only the socket differs. Available only when both players run the Steam build.
    /// Reads happen on the game thread at the start of the frame, like the UDP inbox drain.
    /// </summary>
    public sealed class SteamDatagramSocket : IDatagramSocket
    {
        const int Channel = 0x474F;
        readonly byte[] buf = new byte[2048];
        readonly Callback<P2PSessionRequest_t> onRequest;
        readonly Func<CSteamID, bool> allow;

        public SteamDatagramSocket(Func<CSteamID, bool> allowPeer)
        {
            allow = allowPeer;
            SteamNetworking.AllowP2PPacketRelay(true);
            onRequest = Callback<P2PSessionRequest_t>.Create(r =>
            {
                if (allow(r.m_steamIDRemote)) SteamNetworking.AcceptP2PSessionWithUser(r.m_steamIDRemote);
            });
        }

        public EndPoint LocalEndPoint => new SteamEndPoint(SteamUser.GetSteamID());

        public void SendTo(EndPoint to, byte[] data, int len)
        {
            var id = ((SteamEndPoint)to).Id;
            byte[] packet = data;
            if (len != data.Length) { packet = new byte[len]; Buffer.BlockCopy(data, 0, packet, 0, len); }
            // Unreliable: our own layer does acks and resends. Steam still buffers the first packets while it sets up the route.
            SteamNetworking.SendP2PPacket(id, packet, (uint)len, EP2PSend.k_EP2PSendUnreliable, Channel);
        }

        public bool TryReceive(out EndPoint from, out byte[] data)
        {
            from = null; data = null;
            if (!SteamNetworking.IsP2PPacketAvailable(out uint size, Channel)) return false;
            var b = size <= buf.Length ? buf : new byte[size];
            if (!SteamNetworking.ReadP2PPacket(b, size, out uint got, out CSteamID who, Channel)) return false;
            data = new byte[got];
            Buffer.BlockCopy(b, 0, data, 0, (int)got);
            from = new SteamEndPoint(who);
            return true;
        }

        public void Dispose()
        {
            onRequest?.Unregister();
        }
    }

    /// <summary>
    /// Steam lobbies for invites: the host makes a friends-only lobby and opens the Steam invite dialog;
    /// a friend who accepts (in game, or by launching from the invite) joins the lobby and connects to its owner.
    /// </summary>
    internal sealed class SteamLobby
    {
        public static bool Available
        {
            get
            {
                try { return SteamManager.Initialized; }
                catch { return false; }
            }
        }

        public CSteamID Lobby { get; private set; }
        public event Action<CSteamID> JoinHost;
        Callback<LobbyCreated_t> created;
        Callback<LobbyEnter_t> entered;
        Callback<GameLobbyJoinRequested_t> requested;
        bool hosting;

        public void Init()
        {
            created = Callback<LobbyCreated_t>.Create(OnCreated);
            entered = Callback<LobbyEnter_t>.Create(OnEntered);
            requested = Callback<GameLobbyJoinRequested_t>.Create(r => SteamMatchmaking.JoinLobby(r.m_steamIDLobby));
            // Launched from an invite while the game was closed: Steam passes +connect_lobby <id>.
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out var lobby))
                    SteamMatchmaking.JoinLobby(new CSteamID(lobby));
        }

        public void Host(int maxPlayers)
        {
            hosting = true;
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
        }

        public void Invite()
        {
            if (Lobby.m_SteamID != 0) SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        void OnCreated(LobbyCreated_t r)
        {
            if (r.m_eResult != EResult.k_EResultOK) { Plugin.Log.LogWarning("Steam lobby failed: " + r.m_eResult); return; }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, "go_version", Plugin.Version);
            SteamMatchmaking.SetLobbyData(Lobby, "go_hash", NetSession.GameHash);
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);
            Plugin.Log.LogInfo("Steam lobby ready");
        }

        void OnEntered(LobbyEnter_t r)
        {
            var lobby = new CSteamID(r.m_ulSteamIDLobby);
            if (hosting) return;
            Lobby = lobby;
            var owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (owner == SteamUser.GetSteamID()) return;
            Plugin.Log.LogInfo("Joined Steam lobby; connecting to host");
            JoinHost?.Invoke(owner);
        }

        public void Leave()
        {
            if (Lobby.m_SteamID != 0) SteamMatchmaking.LeaveLobby(Lobby);
            Lobby = default(CSteamID);
            hosting = false;
            try { SteamFriends.ClearRichPresence(); } catch { }
        }
    }
}
