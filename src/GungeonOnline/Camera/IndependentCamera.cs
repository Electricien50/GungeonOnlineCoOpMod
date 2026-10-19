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
            h.Patch(AccessTools.Method(typeof(PlayerController), "HandleCoopSpecificTimers"),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(SkipInSession)));
            // Vanilla co-op stops a player at the edge of the shared camera. With a camera per machine that
            // would pin the other player to the edge of your screen, so it is off online.
            h.Patch(AccessTools.Method(typeof(PlayerController), "CameraBoundsMovementRestrictor"),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(SkipInSession)));
        }

        static bool SkipInSession() => !Session.NetSession.Active;

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
