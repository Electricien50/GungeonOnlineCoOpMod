using Dungeonator;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>Independent cameras allow one player to open a door, and a late teammate to enter an active fight.</summary>
    internal static class CombatDoorEntry
    {
        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(DungeonDoorController), "CheckForPlayerCollision"),
                prefix: new HarmonyMethod(typeof(CombatDoorEntry), nameof(EnterPrefix)));
        }

        static bool EnterPrefix(DungeonDoorController __instance, SpeculativeRigidbody otherRigidbody, Vector2 normal)
        {
            if (!Session.NetSession.Active || !otherRigidbody) return true;
            var p = otherRigidbody.GetComponent<PlayerController>();
            if (!p || p.IsGhost || p.healthHaver.IsDead || __instance.isLocked) return true;
            var mode = __instance.Mode;
            if (mode == DungeonDoorController.DungeonDoorMode.BOSS_DOOR_ONLY_UNSEALS ||
                mode == DungeonDoorController.DungeonDoorMode.FINAL_BOSS_DOOR ||
                mode == DungeonDoorController.DungeonDoorMode.ONE_WAY_DOOR_ONLY_UNSEALS) return true;
            if (!__instance.IsSealed)
            {
                // Vanilla waits for both players to stand at this door, which independent exploration cannot require.
                __instance.Open(__instance.northSouth ? normal.y < 0f : normal.x < 0f);
                return false;
            }
            if (!Session.NetSession.IsHost || GameManager.Instance.IsLoadingLevel) return true;
            var a = __instance.upstreamRoom;
            var b = __instance.downstreamRoom;
            var target = p.CurrentRoom == a ? b : p.CurrentRoom == b ? a : null;
            if (target == null || !target.IsSealed || target.area == null ||
                target.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS) return true;
            PlayerController teammate = null;
            foreach (var q in NPlayers.Players.LivingOthers(p))
                if (q.CurrentRoom == target) { teammate = q; break; }
            // A second fight's empty locked room is still locked. Only admit someone into their teammate's fight.
            if (!teammate) return true;
            p.WarpToPoint(target.GetCenteredVisibleClearSpot(2, 2).ToVector2(), true);
            p.healthHaver.TriggerInvulnerabilityPeriod();
            Plugin.Log.LogInfo("CombatDoorEntry: slot " + PlayerSlots.SlotOf(p) + " entered " + target.GetRoomName());
            return false;
        }
    }
}
