using GungeonOnline.Session;
using HarmonyLib;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Aim assist is a per-player setting, but the game reads it from the local options for every player. While
    /// the game works out where another machine's player is aiming, use that player's own settings, so their
    /// shots go where they went on their screen.
    /// </summary>
    internal static class RemoteAim
    {
        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PlayerController), "DetermineAimPointInWorld"),
                prefix: new HarmonyMethod(typeof(RemoteAim), nameof(Prefix)),
                finalizer: new HarmonyMethod(typeof(RemoteAim), nameof(Finalizer)));
        }

        struct Saved { public bool Active; public float Assist; public bool Beam; public BraveInput.AutoAim Mode; }

        static void Prefix(PlayerController __instance, out Saved __state)
        {
            __state = default(Saved);
            if (!NetSession.Active || !PlayerSlots.IsRemote(__instance) || GameManager.Options == null) return;
            if (!Lobby.Aim.TryGetValue(PlayerSlots.SlotOf(__instance), out var a)) return;
            var o = GameManager.Options;
            __state = new Saved { Active = true, Assist = o.controllerAimAssistMultiplier, Beam = o.controllerBeamAimAssist, Mode = BraveInput.AutoAimMode };
            o.controllerAimAssistMultiplier = a.Assist;
            o.controllerBeamAimAssist = a.Beam;
            BraveInput.AutoAimMode = a.Super ? BraveInput.AutoAim.SuperAutoAim : BraveInput.AutoAim.AutoAim;
        }

        static System.Exception Finalizer(System.Exception __exception, Saved __state)
        {
            if (__state.Active && GameManager.Options != null)
            {
                GameManager.Options.controllerAimAssistMultiplier = __state.Assist;
                GameManager.Options.controllerBeamAimAssist = __state.Beam;
                BraveInput.AutoAimMode = __state.Mode;
            }
            return __exception;
        }
    }
}
