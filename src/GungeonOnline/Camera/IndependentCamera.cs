using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using GungeonOnline.Game;

namespace GungeonOnline.Camera
{
    /// <summary>
    /// Each machine's camera follows only its own player. Vanilla co-op averages both players and pulls
    /// a player who strays into the partner's fight; both are off during an online session.
    /// </summary>
    internal static class IndependentCamera
    {
        public static void Install(Harmony h)
        {
            // Classic co-op style: the vanilla pull into your partner's fight, decided on the host (a guest's
            // character is moved there by the host and follows). Explore and Free for all: no pull.
            h.Patch(AccessTools.Method(typeof(PlayerController), "HandleCoopSpecificTimers"),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(PullPrefix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), nameof(PlayerController.ReuniteWithOtherPlayer)),
                postfix: new HarmonyMethod(typeof(IndependentCamera), nameof(PulledPostfix)));
            // Vanilla co-op stops a player at the edge of the shared camera. With a camera per machine that
            // would pin the other player to the edge of your screen, so it is off online.
            h.Patch(AccessTools.Method(typeof(PlayerController), "CameraBoundsMovementRestrictor"),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(SkipInSession)));
            // The shared camera ignores ghosts; your own camera must keep following you as a ghost.
            h.Patch(AccessTools.Method(typeof(CameraController), nameof(CameraController.GetCoreCurrentBasePosition)),
                transpiler: new HarmonyMethod(typeof(IndependentCamera), nameof(GhostFieldTranspiler)));
            // Vanilla warps a ghost that leaves the shared camera back to its partner; online each has a camera.
            h.Patch(AccessTools.Method(typeof(PlayerController), "Update"),
                transpiler: new HarmonyMethod(typeof(IndependentCamera), nameof(GhostVisibilityTranspiler)));
        }

        static IEnumerable<CodeInstruction> GhostFieldTranspiler(IEnumerable<CodeInstruction> code)
        {
            var field = AccessTools.Field(typeof(PlayerController), nameof(PlayerController.IsGhost));
            var repl = AccessTools.Method(typeof(IndependentCamera), nameof(GhostForCamera));
            foreach (var ci in code)
            {
                if (ci.opcode == OpCodes.Ldfld && ci.operand as System.Reflection.FieldInfo == field)
                    yield return new CodeInstruction(OpCodes.Call, repl) { labels = ci.labels };
                else yield return ci;
            }
        }

        public static bool GhostForCamera(PlayerController p) => p.IsGhost && !(Session.NetSession.Active && p == PlayerSlots.Local);

        static IEnumerable<CodeInstruction> GhostVisibilityTranspiler(IEnumerable<CodeInstruction> code)
        {
            var target = AccessTools.Method(typeof(CameraController), nameof(CameraController.PointIsVisible), new[] { typeof(UnityEngine.Vector2), typeof(float) });
            var repl = AccessTools.Method(typeof(IndependentCamera), nameof(GhostPointVisible));
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand as System.Reflection.MethodInfo == target)
                    yield return new CodeInstruction(OpCodes.Call, repl) { labels = ci.labels };
                else yield return ci;
            }
        }

        public static bool GhostPointVisible(CameraController cc, UnityEngine.Vector2 point, float buffer) =>
            Session.NetSession.Active || cc.PointIsVisible(point, buffer);

        static bool SkipInSession() => !Session.NetSession.Active;

        static bool PullPrefix(PlayerController __instance)
        {
            if (!Session.NetSession.Active) return true;
            if (!Session.NetSession.IsHost || Session.NetConfig.CoopStyle.Value != 0) return false;
            if (!Game.NPlayers.Players.NPlus) return true;
            // Three or four players: the game's timer only follows one partner.
            Game.NPlayers.GatherPatches.PullForN(__instance);
            return false;
        }

        public static int Pulls;

        /// <summary>Count and log every Classic pull (host side), for the tests and for reports.</summary>
        static void PulledPostfix(PlayerController __instance, PlayerController other)
        {
            if (!Session.NetSession.Active) return;
            Pulls++;
            Plugin.Log.LogInfo($"PULL {(Game.PlayerSlots.IsRemote(__instance) ? "guest" : "host")} into {(other != null && other.CurrentRoom != null ? other.CurrentRoom.GetRoomName() : "?")} ghost={__instance.IsGhost}");
        }

        /// <summary>Mark every remote player as ignored by this machine's camera.</summary>
        public static void Apply()
        {
            foreach (var kv in PlayerSlots.Remotes)
                if (kv.Value) PlayerSlots.SetIgnoredByCamera(kv.Value, true);
            if (GameManager.HasInstance && GameManager.Instance.MainCameraController != null)
                GameManager.Instance.MainCameraController.ClearPlayerCache();
        }
    }
}
