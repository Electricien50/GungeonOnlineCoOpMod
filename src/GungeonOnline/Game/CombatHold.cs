using System.Collections.Generic;
using Dungeonator;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Host, Classic co-op: a fight waits for everyone. When one player walks into a room full of enemies, the others
    /// are pulled in a moment later; until every living player is in that room (and has had half a second to land),
    /// its enemies stand still and cannot be hurt, and waves due in the meantime stand still once they appear. The fight
    /// starts for everyone at once. Never longer than the host's FightWaitMaxSeconds, in case someone cannot come
    /// (a fall, a cart). Boss rooms have their own intro, which brings everyone. Guests see it through the host's
    /// enemies like everything else.
    /// </summary>
    internal sealed class CombatHold : MonoBehaviour
    {
        const float LandSeconds = 0.5f;   // after the last player arrives: their landing and input lock (PullLock)
        const string Reason = "gungeon online hold";

        sealed class Wait { public float Since; public float AllInSince = -1f; public bool Done; }
        readonly Dictionary<RoomHandler, Wait> rooms = new Dictionary<RoomHandler, Wait>();
        readonly Dictionary<AIActor, bool> held = new Dictionary<AIActor, bool>(); // enemy -> was it vulnerable
        readonly HashSet<RoomHandler> fights = new HashSet<RoomHandler>();
        readonly List<RoomHandler> stale = new List<RoomHandler>();
        readonly List<AIActor> drop = new List<AIActor>();
        public static int Holds;

        static bool On => NetSession.Active && NetSession.IsHost && NetConfig.CoopStyle.Value == 0 && NetConfig.FightsWaitForAll.Value &&
            GameManager.HasInstance && GameManager.Instance.Dungeon != null && !GameManager.Instance.IsLoadingLevel;

        void Update()
        {
            if (!On) { if (held.Count > 0 || rooms.Count > 0) { ReleaseAll(); rooms.Clear(); } return; }
            float now = Time.realtimeSinceStartup;
            // The fights going on: sealed rooms (not a boss's) with enemies left, that a living player is in.
            fights.Clear();
            foreach (var p in NPlayers.Players.All)
            {
                if (!NPlayers.Players.IsLiving(p)) continue;
                var room = p.CurrentRoom;
                if (room == null || !room.IsSealed || fights.Contains(room)) continue;
                if (room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS) continue;
                if (!room.HasActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear)) continue;
                fights.Add(room);
            }
            // A room that is no longer a fight starts over next time.
            stale.Clear();
            foreach (var kv in rooms) if (!fights.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var r in stale) { ReleaseRoom(r); rooms.Remove(r); }

            foreach (var room in fights)
            {
                if (!rooms.TryGetValue(room, out var w))
                {
                    rooms[room] = w = new Wait { Since = now };
                    Plugin.Log.LogInfo("CombatHold: " + room.GetRoomName() + " waits for everyone");
                }
                if (w.Done) continue;
                bool everyone = true;
                foreach (var p in NPlayers.Players.All)
                    if (NPlayers.Players.IsLiving(p) && p.CurrentRoom != room) { everyone = false; break; }
                if (!everyone) w.AllInSince = -1f;
                else if (w.AllInSince < 0f) w.AllInSince = now;
                bool timedOut = now - w.Since >= Mathf.Max(0f, NetConfig.FightWaitMax.Value);
                if (timedOut || (everyone && now - w.AllInSince >= LandSeconds))
                {
                    w.Done = true;
                    ReleaseRoom(room);
                    Plugin.Log.LogInfo($"CombatHold: {room.GetRoomName()} starts after {now - w.Since:F1}s{(timedOut && !everyone ? " (not everyone came)" : "")}");
                    continue;
                }
                HoldRoom(room);
            }
        }

        void HoldRoom(RoomHandler room)
        {
            var list = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            if (list == null) return;
            foreach (var a in list)
            {
                // An enemy still arriving (spawn marker) is already out of reach; it is held once it shows.
                if (!a || a.IsGone || a.healthHaver == null || a.healthHaver.IsDead) continue;
                if (!held.ContainsKey(a))
                {
                    held[a] = a.healthHaver.IsVulnerable;
                    if (a.knockbackDoer) a.knockbackDoer.SetImmobile(true, Reason);
                    Holds++;
                }
                // The AI thinks again once this runs out (a fifth of a second after the hold ends).
                a.SpeculatorDelayTime = Mathf.Max(a.SpeculatorDelayTime, 0.2f);
                a.ClearPath();
                a.healthHaver.IsVulnerable = false;
            }
        }

        void ReleaseRoom(RoomHandler room)
        {
            drop.Clear();
            foreach (var kv in held) if (!kv.Key || kv.Key.ParentRoom == room) drop.Add(kv.Key);
            foreach (var a in drop) Release(a);
        }

        void ReleaseAll()
        {
            drop.Clear();
            drop.AddRange(held.Keys);
            foreach (var a in drop) Release(a);
        }

        void Release(AIActor a)
        {
            bool vulnerable = held.TryGetValue(a, out var v) && v;
            held.Remove(a);
            if (!a) return;
            if (a.knockbackDoer) a.knockbackDoer.SetImmobile(false, Reason);
            if (vulnerable && a.healthHaver != null && !a.healthHaver.IsDead) a.healthHaver.IsVulnerable = true;
            a.SpeculatorDelayTime = 0f;
        }
    }
}
