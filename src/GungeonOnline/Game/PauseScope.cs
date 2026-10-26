using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Pausing online. Opening the pause menu (or the map's pause) shows the menu but does not stop the world,
    /// because the world is shared; only your own character waits. Other players' characters keep running on
    /// your machine. Death and game-over pauses are untouched. Host option PauseFreezesEveryone restores vanilla.
    /// </summary>
    internal static class PauseScope
    {
        static bool Online => Session.NetSession.Active && !Session.NetConfig.PauseFreezesEveryone.Value;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.Pause)),
                postfix: new HarmonyMethod(typeof(PauseScope), nameof(PausePostfix)));
            var tr = new HarmonyMethod(typeof(PauseScope), nameof(Transpile));
            h.Patch(AccessTools.Method(typeof(PlayerController), "Update"), transpiler: tr);
            h.Patch(AccessTools.Method(typeof(PlayerController), "LateUpdate"), transpiler: tr);
        }

        /// <summary>The menu is open, but online the clock keeps running.</summary>
        static void PausePostfix(GameManager __instance)
        {
            if (!Online) return;
            BraveTime.SetTimeScaleMultiplier(1f, __instance.gameObject);
        }

        /// <summary>Replaces "GameManager.Instance.IsPaused" in player updates with "is this player's machine paused".</summary>
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> code)
        {
            var getter = AccessTools.PropertyGetter(typeof(GameManager), nameof(GameManager.IsPaused));
            var repl = AccessTools.Method(typeof(PauseScope), nameof(PausedFor));
            int n = 0;
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand as System.Reflection.MethodInfo == getter)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, repl);
                    n++;
                    continue;
                }
                yield return ci;
            }
        }

        public static bool PausedFor(GameManager gm, PlayerController p)
        {
            if (!gm.IsPaused) return false;
            if (!Online) return true;
            // Online, a paused menu only stops this machine's own player.
            return p == PlayerSlots.Local;
        }

        /// <summary>True while this machine has its pause menu or the online menu open in a session (used to send
        /// neutral input: moving through a menu must not walk or shoot your character on the other screens).</summary>
        public static bool LocalMenuOpen => Session.NetSession.Active && (Session.OnlineMenu.IsOpen || GameManager.HasInstance && GameManager.Instance.IsPaused);
    }
}
