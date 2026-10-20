using System;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game.NPlayers
{
    /// <summary>
    /// The game's player list and its "best" and "random" player helpers, taught about more than two players.
    /// Nothing here changes a two-player session.
    /// </summary>
    internal static class PlayerListPatches
    {
        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.PropertyGetter(typeof(GameManager), nameof(GameManager.AllPlayers)),
                postfix: new HarmonyMethod(typeof(PlayerListPatches), nameof(AllPlayersPostfix)));
            h.Patch(AccessTools.PropertyGetter(typeof(GameManager), nameof(GameManager.BestActivePlayer)),
                postfix: new HarmonyMethod(typeof(PlayerListPatches), nameof(BestActivePostfix)));
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.GetRandomActivePlayer)),
                postfix: new HarmonyMethod(typeof(PlayerListPatches), nameof(RandomActivePostfix)));
        }

        /// <summary>
        /// The game only puts player 0 first when there are exactly two players; with more, the list comes back in
        /// scene order. Sort the cached array by player id in place, so index == id everywhere the game indexes it.
        /// (Checks the order first: this getter is called many times a frame.)
        /// </summary>
        static void AllPlayersPostfix(PlayerController[] __result)
        {
            if (__result == null || __result.Length <= 2 || !Session.NetSession.Active) return;
            bool sorted = true;
            for (int i = 1; i < __result.Length && sorted; i++)
                if (__result[i] && __result[i - 1] && __result[i].PlayerIDX < __result[i - 1].PlayerIDX) sorted = false;
            if (sorted) return;
            Array.Sort(__result, (a, b) => (a ? a.PlayerIDX : 99).CompareTo(b ? b.PlayerIDX : 99));
        }

        /// <summary>Vanilla: player 0, or player 1 if player 0 is down. With more players: the first one still standing.</summary>
        static void BestActivePostfix(ref PlayerController __result)
        {
            if (!Players.NPlus || Players.IsLiving(__result)) return;
            foreach (var p in Players.All) if (Players.IsLiving(p)) { __result = p; return; }
        }

        /// <summary>Vanilla picks between players 0 and 1. With more players: any living one, at random.</summary>
        static void RandomActivePostfix(ref PlayerController __result)
        {
            if (!Players.NPlus) return;
            var living = new System.Collections.Generic.List<PlayerController>();
            foreach (var p in Players.All) if (Players.IsLiving(p)) living.Add(p);
            if (living.Count > 0) __result = living[UnityEngine.Random.Range(0, living.Count)];
        }
    }
}
