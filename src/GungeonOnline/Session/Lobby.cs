using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Net;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Who is in the session and which character each player picked. Each player picks in their own Breach;
    /// the host keeps the roster and sends it to everyone; every machine spawns the others' characters.
    /// </summary>
    internal sealed class Lobby : MonoBehaviour
    {
        const byte MsgChoice = 1, MsgRoster = 2, MsgAim = 3, MsgStyle = 4;

        public sealed class Entry { public string Prefab = ""; public string Name = ""; }
        public readonly Dictionary<int, Entry> Roster = new Dictionary<int, Entry>();

        /// <summary>Each player's own aim assist settings, so every machine aims their copy the way they aim it.</summary>
        public struct AimPrefs { public float Assist; public bool Beam; public bool Super; }
        public static readonly Dictionary<int, AimPrefs> Aim = new Dictionary<int, AimPrefs>();
        string sentAim;

        /// <summary>Guest: the co-op style the host chose (-1 until it arrives).</summary>
        public static int HostCoopStyle = -1;
        int sentStyle = -1;
        float nextAimCheck;
        string sentChoice;
        PlayerController lastLocal;

        void Start()
        {
            var s = NetSession.Instance;
            s.On(Channel.Lobby, OnMessage);
            s.PeerJoined += c => { if (NetSession.IsHost) { SendRoster(); SendAllAim(c); sentStyle = -1; } else sentChoice = null; sentAim = null; };
            s.PeerLeft += (c, why) =>
            {
                if (!NetSession.IsHost) return;
                // During a run a player who drops stays in the game (their character waits for them).
                if (!Game.SceneObjects.InBreach && GameManager.HasInstance && GameManager.Instance.Dungeon != null) return;
                Roster.Remove(c.Slot);
                PlayerSlots.Despawn(c.Slot);
                SendRoster();
            };
            s.SessionEnded += () => { Roster.Clear(); sentChoice = null; sentAim = null; Aim.Clear(); HostCoopStyle = -1; sentStyle = -1; PlayerSlots.Clear(); };
        }

        static string PrefabOf(PlayerController p)
        {
            var n = p.gameObject.name;
            int i = n.IndexOf("(Clone)");
            return i >= 0 ? n.Substring(0, i).Trim() : n;
        }

        void Update()
        {
            if (!NetSession.Active) return;
            var local = PlayerSlots.Local;
            if (local != lastLocal)
            {
                string desc = "";
                if (GameManager.HasInstance && GameManager.Instance.AllPlayers != null)
                    foreach (var p in GameManager.Instance.AllPlayers) if (p) desc += $" [{p.name} idx={p.PlayerIDX} id={p.GetInstanceID()}]";
                Plugin.Log.LogInfo($"LOCALCHANGE scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} now={(local ? local.name + " id=" + local.GetInstanceID() : "none")} all:{desc}");
                lastLocal = local;
                // A character swap at a Breach statue replaces the player object; drop the stale one from the list.
                if (GameManager.HasInstance) GameManager.Instance.RefreshAllPlayers();
            }
            // Characters are chosen in the Breach only; never re-announce mid-run.
            if (local != null && Game.SceneObjects.InBreach)
            {
                string mine = PrefabOf(local);
                if (mine != sentChoice)
                {
                    sentChoice = mine;
                    if (NetSession.IsHost)
                    {
                        Roster[0] = new Entry { Prefab = mine, Name = NetConfig.PlayerName.Value };
                        SendRoster();
                    }
                    else
                    {
                        var w = new WireWriter(64);
                        w.U8(MsgChoice); w.Str(mine); w.Str(NetConfig.PlayerName.Value);
                        NetSession.Instance.Node.Broadcast((byte)Channel.Lobby, w.ToArray(), Delivery.ReliableOrdered);
                    }
                }
            }
            if (NetSession.IsHost && NetConfig.CoopStyle.Value != sentStyle) SendStyle();
            if (Time.realtimeSinceStartup >= nextAimCheck && local != null) { nextAimCheck = Time.realtimeSinceStartup + 1f; CheckAim(); }
            // Spawn anyone we know about but have not spawned (our own player may have appeared late).
            if (local != null)
                foreach (var kv in Roster)
                    if (kv.Key != NetSession.LocalSlot && kv.Value.Prefab.Length > 0 && PlayerSlots.ForSlot(kv.Key) == null)
                        PlayerSlots.SpawnRemote(kv.Key, kv.Value.Prefab);
        }

        static AimPrefs MyAim()
        {
            var o = GameManager.Options;
            bool mouse = false;
            var me = PlayerSlots.Local;
            try { if (me != null) mouse = BraveInput.GetInstanceForPlayer(me.PlayerIDX).IsKeyboardAndMouse(); } catch { }
            // Mouse aim is already exact (it is sent as a direction); no assist on the other side either.
            if (o == null || mouse) return new AimPrefs { Assist = 0f, Beam = false, Super = false };
            bool super = false;
            try { super = BraveInput.AutoAimMode == BraveInput.AutoAim.SuperAutoAim; } catch { } // no input instance yet at boot
            return new AimPrefs { Assist = o.controllerAimAssistMultiplier, Beam = o.controllerBeamAimAssist, Super = super };
        }

        void CheckAim()
        {
            var a = MyAim();
            string key = a.Assist.ToString("F2") + a.Beam + a.Super;
            if (key == sentAim) return;
            sentAim = key;
            Aim[NetSession.LocalSlot] = a;
            NetSession.Instance.Node.Broadcast((byte)Channel.Lobby, AimMsg(NetSession.LocalSlot, a), Delivery.ReliableOrdered);
        }

        static byte[] AimMsg(int slot, AimPrefs a)
        {
            var w = new WireWriter(16);
            w.U8(MsgAim); w.U8((byte)slot); w.F32(a.Assist); w.Bool(a.Beam); w.Bool(a.Super);
            return w.ToArray();
        }

        void SendAllAim(Connection to)
        {
            foreach (var kv in Aim) if (kv.Key != to.Slot) to.Send((byte)Channel.Lobby, AimMsg(kv.Key, kv.Value), Delivery.ReliableOrdered);
        }

        void SendStyle()
        {
            sentStyle = NetConfig.CoopStyle.Value;
            var w = new WireWriter(4);
            w.U8(MsgStyle); w.U8((byte)sentStyle);
            NetSession.Instance.Node.Broadcast((byte)Channel.Lobby, w.ToArray(), Delivery.ReliableOrdered);
        }

        public void SendRoster()
        {
            var w = new WireWriter(128);
            w.U8(MsgRoster);
            w.U8((byte)Roster.Count);
            foreach (var kv in Roster) { w.U8((byte)kv.Key); w.Str(kv.Value.Prefab); w.Str(kv.Value.Name); }
            NetSession.Instance.Node.Broadcast((byte)Channel.Lobby, w.ToArray(), Delivery.ReliableOrdered);
        }

        void OnMessage(Connection c, byte[] data, Delivery d)
        {
            var r = new WireReader(data, 0, data.Length);
            byte type = r.U8();
            if (type == MsgStyle && !NetSession.IsHost) { int v = r.U8(); if (HostCoopStyle >= 0 && v != HostCoopStyle) Toasts.Show("The host switched to " + (v == 0 ? "Classic" : "Explore") + " co-op"); HostCoopStyle = v; return; }
            if (type == MsgAim)
            {
                int slot = r.U8();
                var a = new AimPrefs { Assist = r.F32(), Beam = r.Bool(), Super = r.Bool() };
                if (NetSession.IsHost) slot = c.Slot;
                if (slot == NetSession.LocalSlot) return;
                Aim[slot] = a;
                if (NetSession.IsHost)
                    foreach (var o in NetSession.Instance.Node.Connections)
                        if (o != c) o.Send((byte)Channel.Lobby, AimMsg(slot, a), Delivery.ReliableOrdered);
                return;
            }
            if (type == MsgChoice && NetSession.IsHost)
            {
                Roster[c.Slot] = new Entry { Prefab = r.Str(), Name = r.Str() };
                Plugin.Log.LogInfo($"Lobby: slot {c.Slot} picked {Roster[c.Slot].Prefab}");
                SendRoster();
            }
            else if (type == MsgRoster && !NetSession.IsHost)
            {
                int n = r.U8();
                var seen = new HashSet<int>();
                for (int i = 0; i < n; i++)
                {
                    int slot = r.U8();
                    Roster[slot] = new Entry { Prefab = r.Str(), Name = r.Str() };
                    seen.Add(slot);
                }
                foreach (var slot in new List<int>(Roster.Keys))
                    if (!seen.Contains(slot)) { Roster.Remove(slot); PlayerSlots.Despawn(slot); }
            }
            // Re-spawn with a new character if someone changed theirs.
            foreach (var kv in Roster)
                if (kv.Key != NetSession.LocalSlot && kv.Value.Prefab.Length > 0)
                    PlayerSlots.SpawnRemote(kv.Key, kv.Value.Prefab);
        }
    }
}
