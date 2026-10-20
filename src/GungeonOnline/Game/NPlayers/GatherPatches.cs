using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game.NPlayers
{
    /// <summary>
    /// Moves that bring "the co-op partner" along (warps, teleporters, the Classic pull into a fight) bring every
    /// other player with three or four. A guard stops the players from bringing each other in a loop: the game
    /// drops its own guard before our code runs.
    /// </summary>
    internal static class GatherPatches
    {
        static bool gathering;
        static readonly Dictionary<PlayerController, float> pullTimers = new Dictionary<PlayerController, float>();

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PlayerController), nameof(PlayerController.WarpToPointAndBringCoopPartner)),
                postfix: new HarmonyMethod(typeof(GatherPatches), nameof(WarpPostfix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), nameof(PlayerController.TeleportToPoint)),
                postfix: new HarmonyMethod(typeof(GatherPatches), nameof(TeleportPostfix)));
            // Boss intros place "both" players at their marks: place the rest beside player 2's mark.
            foreach (var t in new[] { typeof(GenericIntroDoer), typeof(GatlingGullIntroDoer) })
            {
                var m = AccessTools.Method(t, "TeleportCoopPlayers");
                if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(GatherPatches), nameof(IntroPlacePostfix)));
            }
        }

        static void WarpPostfix(PlayerController __instance, Vector2 targetPoint, bool useDefaultPoof, bool doFollowers)
        {
            if (!Players.NPlus || gathering) return;
            gathering = true;
            try
            {
                var partner = GameManager.Instance.GetOtherPlayer(__instance); // the game already moved this one
                Vector2 mine = __instance.specRigidbody.UnitBottomLeft - __instance.transform.position.XY();
                foreach (var p in Players.Others(__instance))
                {
                    if (p == partner || !p.specRigidbody) continue;
                    Vector2 theirs = p.specRigidbody.UnitBottomLeft - p.transform.position.XY();
                    p.WarpToPoint(targetPoint + (mine - theirs), useDefaultPoof, doFollowers);
                }
            }
            finally { gathering = false; }
        }

        static void TeleportPostfix(PlayerController __instance, Vector2 targetPoint, bool useDefaultTeleportVFX)
        {
            if (!Players.NPlus || gathering) return;
            gathering = true;
            try
            {
                var partner = GameManager.Instance.GetOtherPlayer(__instance);
                foreach (var p in Players.Others(__instance))
                    if (p != partner) p.TeleportToPoint(targetPoint, useDefaultTeleportVFX);
            }
            finally { gathering = false; }
        }

        static void IntroPlacePostfix(object __instance)
        {
            if (!Players.NPlus) return;
            var marks = AccessTools.Field(__instance.GetType(), "m_idealStartingPositions")?.GetValue(__instance) as Vector2[];
            if (marks == null || marks.Length < 2) return;
            var gm = GameManager.Instance;
            int n = 0;
            foreach (var p in Players.All)
            {
                if (p == gm.PrimaryPlayer || p == gm.SecondaryPlayer || p.IsGhost) continue;
                n++;
                // Fan out from player 2's mark, away from player 1's.
                Vector2 away = (marks[1] - marks[0]).normalized;
                if (away.sqrMagnitude < 0.01f) away = Vector2.right;
                Vector2 spot = marks[1] + away * (0.9f * n);
                if (Vector2.Distance(p.CenterPosition, spot) > 2f) p.WarpToPoint(spot, true);
            }
        }

        /// <summary>
        /// Classic style with three or four players: a player outside any sealed fight is pulled, after a second,
        /// into the sealed room holding the most living players. Replaces the game's own "follow the other player"
        /// timer (which only knows one partner). Host only, like the two-player pull.
        /// </summary>
        public static void PullForN(PlayerController me)
        {
            Dungeonator.RoomHandler target = null;
            int best = 0;
            foreach (var p in Players.LivingOthers(me))
            {
                var room = p.CurrentRoom;
                if (room == null || !room.IsSealed || room == me.CurrentRoom) continue;
                int n = 0;
                foreach (var q in Players.All) if (Players.IsLiving(q) && q.CurrentRoom == room) n++;
                if (n > best) { best = n; target = room; }
            }
            if (target == null) { pullTimers[me] = 0f; return; }
            pullTimers.TryGetValue(me, out var t);
            t += BraveTime.DeltaTime;
            if (t <= 1f) { pullTimers[me] = t; return; }
            pullTimers[me] = 0f;
            PlayerController anchor = null;
            foreach (var q in Players.LivingOthers(me)) if (q.CurrentRoom == target) { anchor = q; break; }
            if (anchor == null) return;
            if (me.IsGhost) LootEngine.DoDefaultPurplePoof(me.CenterPosition, ignoreTimeScale: true);
            me.ReuniteWithOtherPlayer(anchor);
            if (me.IsGhost) LootEngine.DoDefaultPurplePoof(me.CenterPosition, ignoreTimeScale: true);
            me.healthHaver.TriggerInvulnerabilityPeriod();
        }
    }
}
