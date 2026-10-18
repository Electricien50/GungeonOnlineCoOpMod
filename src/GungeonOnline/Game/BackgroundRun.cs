using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Vanilla pauses the game when its window loses focus. In an online session that would freeze the host
    /// for everyone on alt-tab, so while a session (or the test harness) is active the game keeps running.
    /// </summary>
    internal static class BackgroundRun
    {
        /// <summary>True while a network session is live. Set by the session layer.</summary>
        public static bool SessionActive;

        static bool Active => SessionActive || Dev.DevEnv.IsHarness;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.OnApplicationFocus)),
                prefix: new HarmonyMethod(typeof(BackgroundRun), nameof(FocusPrefix)));
            if (Dev.DevEnv.IsHarness) Application.runInBackground = true;
        }

        public static void Refresh()
        {
            if (Active) Application.runInBackground = true;
        }

        static bool FocusPrefix() => !Active;
    }
}
