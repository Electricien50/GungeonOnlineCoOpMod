using System.Collections.Generic;
using Dungeonator;
using HarmonyLib;

namespace GungeonOnline.Game
{
    /// <summary>
    /// With independent cameras two players could start two fights at once. While any room is sealed in combat,
    /// every other room that still holds enemies (and has no player inside) is sealed too, and reopens when the
    /// fight ends. Host only; the seals replicate like any other. Boss rooms and special doors are left alone.
    /// Config: Gameplay.SecondFightLock (default on).
    /// </summary>
    internal static class SecondFightLock
    {
        static readonly HashSet<RoomHandler> lockedByUs = new HashSet<RoomHandler>();
        static RoomHandler fight;
        static bool busy;

        static bool Enabled => Session.NetSession.Active && Session.NetSession.IsHost && Session.NetConfig.CoopStyle.Value != 2;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(RoomHandler), nameof(RoomHandler.SealRoom)), postfix: new HarmonyMethod(typeof(SecondFightLock), nameof(AfterSeal)));
            h.Patch(AccessTools.Method(typeof(RoomHandler), nameof(RoomHandler.UnsealRoom)), postfix: new HarmonyMethod(typeof(SecondFightLock), nameof(AfterUnseal)));
        }

        public static void Reset() { lockedByUs.Clear(); fight = null; }

        static bool PlayerInside(RoomHandler r)
        {
            foreach (var p in GameManager.Instance.AllPlayers) if (p && p.CurrentRoom == r) return true;
            return false;
        }

        static bool Eligible(RoomHandler r)
        {
            if (r == null || r.IsSealed || r.area == null) return false;
            if (r.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS) return false;
            if (r.connectedDoors != null)
                foreach (var d in r.connectedDoors)
                    if (d != null && (d.Mode == DungeonDoorController.DungeonDoorMode.BOSS_DOOR_ONLY_UNSEALS || d.Mode == DungeonDoorController.DungeonDoorMode.FINAL_BOSS_DOOR || d.Mode == DungeonDoorController.DungeonDoorMode.ONE_WAY_DOOR_ONLY_UNSEALS)) return false;
            var enemies = r.GetActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear);
            return enemies != null && enemies.Count > 0 && !PlayerInside(r);
        }

        static void AfterSeal(RoomHandler __instance)
        {
            if (!Enabled || busy || lockedByUs.Contains(__instance) || fight != null) return;
            fight = __instance;
            busy = true;
            try
            {
                foreach (var r in GameManager.Instance.Dungeon.data.rooms)
                    if (r != __instance && Eligible(r)) { r.SealRoom(); lockedByUs.Add(r); }
            }
            finally { busy = false; }
            if (lockedByUs.Count > 0) Plugin.Log.LogInfo($"SecondFightLock: {lockedByUs.Count} other rooms held shut during the fight in {__instance.GetRoomName()}");
        }

        static void AfterUnseal(RoomHandler __instance)
        {
            if (!Enabled || busy || __instance != fight) return;
            fight = null;
            busy = true;
            try { foreach (var r in lockedByUs) if (r != null) r.UnsealRoom(); }
            finally { busy = false; lockedByUs.Clear(); }
        }
    }
}
