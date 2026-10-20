using HarmonyLib;

namespace GungeonOnline.Game.NPlayers
{
    /// <summary>
    /// Rooms and the players in them, for more than two players. The game checks "the other player" when it
    /// decides a room was left, may be switched off, or just got cleared; here it checks everyone.
    /// </summary>
    internal static class RoomPresencePatches
    {
        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Dungeonator.RoomHandler), nameof(Dungeonator.RoomHandler.PlayerExit)),
                prefix: new HarmonyMethod(typeof(RoomPresencePatches), nameof(ExitPrefix)));
            h.Patch(AccessTools.Method(typeof(Dungeonator.RoomHandler), nameof(Dungeonator.RoomHandler.SetRoomActive)),
                prefix: new HarmonyMethod(typeof(RoomPresencePatches), nameof(SetActivePrefix)));
            h.Patch(AccessTools.Method(typeof(Dungeonator.RoomHandler), "NotifyPlayerRoomCleared"),
                postfix: new HarmonyMethod(typeof(RoomPresencePatches), nameof(ClearedPostfix)));
        }

        /// <summary>Someone other than <paramref name="except"/> is in the room, or standing in one of its doorways.</summary>
        static bool OccupiedByOther(Dungeonator.RoomHandler room, PlayerController except)
        {
            foreach (var p in Players.Others(except))
            {
                if (p.healthHaver != null && p.healthHaver.IsDead && !p.IsGhost) continue;
                if (p.CurrentRoom == room) return true;
                var cell = p.InExitCell ? p.CurrentExitCell : null;
                if (cell != null && cell.exitDoor && (cell.exitDoor.upstreamRoom == room || cell.exitDoor.downstreamRoom == room)) return true;
            }
            return false;
        }

        /// <summary>
        /// Every frame each player "exits" any lit room that is not its own or its partner's. With three or four, that
        /// keeps firing on rooms where players 3 and 4 stand (minimap flag off, exit events). Nobody exits a room
        /// that another player is still in.
        /// </summary>
        static bool ExitPrefix(Dungeonator.RoomHandler __instance, PlayerController playerExiting)
        {
            if (!Players.NPlus) return true;
            return !OccupiedByOther(__instance, playerExiting);
        }

        /// <summary>The game switches off rooms far from "the two players". A room with any player in it stays on.</summary>
        static bool SetActivePrefix(Dungeonator.RoomHandler __instance, bool active, ref bool __result)
        {
            if (active || !Players.NPlus) return true;
            foreach (var p in Players.All)
                if (p.CurrentRoom == __instance && !(p.healthHaver != null && p.healthHaver.IsDead && !p.IsGhost)) { __result = false; return false; }
            return true;
        }

        /// <summary>Room cleared: the game tells players 0 and 1 (active item charge, clear events). Tell the rest too.</summary>
        static void ClearedPostfix()
        {
            if (!Players.NPlus) return;
            var gm = GameManager.Instance;
            foreach (var p in Players.All)
                if (p != gm.PrimaryPlayer && p != gm.SecondaryPlayer) p.OnRoomCleared();
        }
    }
}
