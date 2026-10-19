using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Progress for the guest's own save. In local co-op both players share one save; online each machine has its
    /// own, and the run happens on the host. So the host forwards the progress a run earns (Hegemony credits,
    /// kill counts, unlock flags such as bosses killed) and the guest's save records it too. The guest's game
    /// never counts these by itself during a run (its enemies are copies), so nothing is counted twice.
    /// </summary>
    internal static class StatSync
    {
        public const byte EvStat = 58;
        static bool scope;
        public static int Sent, Applied;

        static bool InRun => GameManager.HasInstance && GameManager.Instance.Dungeon != null && !SceneObjects.InBreach;

        static bool Forwarded(TrackedStats s)
        {
            if (s == TrackedStats.META_CURRENCY || s == TrackedStats.TIMES_KILLED_PAST) return true;
            return s.ToString().Contains("KILLED");
        }

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameStatsManager), nameof(GameStatsManager.RegisterStatChange)),
                prefix: new HarmonyMethod(typeof(StatSync), nameof(StatPrefix)),
                postfix: new HarmonyMethod(typeof(StatSync), nameof(StatPostfix)));
            h.Patch(AccessTools.Method(typeof(GameStatsManager), nameof(GameStatsManager.SetFlag)),
                postfix: new HarmonyMethod(typeof(StatSync), nameof(FlagPostfix)));
        }

        static bool StatPrefix(TrackedStats stat)
        {
            if (!NetSession.Active || NetSession.IsHost || scope || !InRun) return true;
            return !Forwarded(stat);
        }

        static void StatPostfix(TrackedStats stat, float value)
        {
            if (!NetSession.Active || !NetSession.IsHost || !InRun || !Forwarded(stat) || value == 0f) return;
            Send(0, (int)stat, value);
        }

        static void FlagPostfix(GungeonFlags flag, bool value)
        {
            if (!NetSession.Active || !NetSession.IsHost || !InRun || !value || flag == GungeonFlags.NONE) return;
            Send(1, (int)flag, 1f);
        }

        static void Send(byte kind, int what, float value)
        {
            var w = new WireWriter(16);
            w.U8(EvStat); w.U8(kind); w.I32(what); w.F32(value);
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
            Sent++;
        }

        public static void Apply(ref WireReader r)
        {
            byte kind = r.U8();
            int what = r.I32();
            float value = r.F32();
            if (NetSession.IsHost || GameStatsManager.Instance == null) return;
            scope = true;
            try
            {
                if (kind == 0) GameStatsManager.Instance.RegisterStatChange((TrackedStats)what, value);
                else GameStatsManager.Instance.SetFlag((GungeonFlags)what, true);
                Applied++;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("StatSync: " + e.Message); }
            finally { scope = false; }
        }
    }
}
