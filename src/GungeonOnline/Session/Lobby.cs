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
        const byte MsgChoice = 1, MsgRoster = 2;

        public sealed class Entry { public string Prefab = ""; public string Name = ""; }
        public readonly Dictionary<int, Entry> Roster = new Dictionary<int, Entry>();
        string sentChoice;
        PlayerController lastLocal;

        void Start()
        {
            var s = NetSession.Instance;
            s.On(Channel.Lobby, OnMessage);
            s.PeerJoined += c => { if (NetSession.IsHost) SendRoster(); else sentChoice = null; };
            s.PeerLeft += (c, why) =>
            {
                if (!NetSession.IsHost) return;
                Roster.Remove(c.Slot);
                PlayerSlots.Despawn(c.Slot);
                SendRoster();
            };
            s.SessionEnded += () => { Roster.Clear(); sentChoice = null; PlayerSlots.Clear(); };
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
            // Spawn anyone we know about but have not spawned (our own player may have appeared late).
            if (local != null)
                foreach (var kv in Roster)
                    if (kv.Key != NetSession.LocalSlot && kv.Value.Prefab.Length > 0 && PlayerSlots.ForSlot(kv.Key) == null)
                        PlayerSlots.SpawnRemote(kv.Key, kv.Value.Prefab);
        }

        void SendRoster()
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
