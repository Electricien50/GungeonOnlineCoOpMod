using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Harness checks for how a player looks on the other screens.
    /// GO_ALT_COSTUME=host|guest: that player wears the alternate costume (picked in the Breach).
    /// GO_FIRE_AT=seconds (with GO_FIRE_WHO=host|guest): that player catches fire then, and goes out 6 s later.
    /// Every 2 s each game logs "LOOKCHECK" with every player's costume and fire state as it sees them.
    /// </summary>
    internal static class LookCheck
    {
        static bool costumeDone, fireOn, fireOff;
        static float nextLog;

        static bool Me(string who) => who == (Session.NetSession.IsHost ? "host" : "guest");

        public static void Tick()
        {
            if (!DevEnv.IsHarness || !Session.NetSession.Active || !GameManager.HasInstance) return;
            var me = Game.PlayerSlots.Local;
            if (!me) return;
            float t = Time.realtimeSinceStartup;
            if (!costumeDone && Game.SceneObjects.InBreach && Me(System.Environment.GetEnvironmentVariable("GO_ALT_COSTUME") ?? "") && t > 12f)
            {
                costumeDone = true;
                if (!me.IsUsingAlternateCostume) me.SwapToAlternateCostume();
                Plugin.Log.LogInfo("LOOKCHECK put on the alternate costume alt=" + me.IsUsingAlternateCostume);
            }
            if (float.TryParse(System.Environment.GetEnvironmentVariable("GO_FIRE_AT"), out var at) && Me(System.Environment.GetEnvironmentVariable("GO_FIRE_WHO") ?? "guest")
                && !Game.SceneObjects.InBreach && !GameManager.Instance.IsLoadingLevel)
            {
                if (!fireOn && t >= at) { fireOn = true; me.healthHaver.IsVulnerable = false; me.IsOnFire = true; Plugin.Log.LogInfo("LOOKCHECK set myself on fire"); }
                if (fireOn && !fireOff && t >= at + 6f) { fireOff = true; me.IsOnFire = false; Plugin.Log.LogInfo("LOOKCHECK put my fire out"); }
                // Keep it burning for the 6 s (the game puts out a fire that is not fed by flames on the floor).
                if (fireOn && !fireOff && !me.IsOnFire) me.IsOnFire = true;
            }
            if (t < nextLog) return;
            nextLog = t + 2f;
            var sb = new System.Text.StringBuilder("LOOKCHECK");
            foreach (var p in GameManager.Instance.AllPlayers)
                if (p) sb.Append($" [{(p == me ? "me" : "copy")} slot={Game.PlayerSlots.SlotOf(p)} {p.name.Replace("(Clone)", "")} alt={p.IsUsingAlternateCostume} fire={p.IsOnFire}]");
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
