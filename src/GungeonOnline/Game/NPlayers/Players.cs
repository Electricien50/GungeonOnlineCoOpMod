using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline.Game.NPlayers
{
    /// <summary>
    /// The game thinks in two players ("the other player"). With three or four, the questions become "any other
    /// player", "the nearest living one", "every ghost". These answer them from the game's own player list.
    /// </summary>
    internal static class Players
    {
        /// <summary>More than two players in an online session: the point where the game's two-player rules need help.</summary>
        public static bool NPlus => Session.NetSession.Active && GameManager.HasInstance && GameManager.Instance.AllPlayers != null && GameManager.Instance.AllPlayers.Length > 2;

        public static int Count => GameManager.HasInstance && GameManager.Instance.AllPlayers != null ? GameManager.Instance.AllPlayers.Length : 0;

        public static IEnumerable<PlayerController> All
        {
            get
            {
                if (!GameManager.HasInstance || GameManager.Instance.AllPlayers == null) yield break;
                foreach (var p in GameManager.Instance.AllPlayers) if (p) yield return p;
            }
        }

        public static IEnumerable<PlayerController> Others(PlayerController me)
        {
            foreach (var p in All) if (p != me) yield return p;
        }

        public static bool IsLiving(PlayerController p) => p && !p.IsGhost && p.healthHaver != null && !p.healthHaver.IsDead;

        public static IEnumerable<PlayerController> LivingOthers(PlayerController me)
        {
            foreach (var p in Others(me)) if (IsLiving(p)) yield return p;
        }

        public static bool AnyOtherAlive(PlayerController me)
        {
            foreach (var p in LivingOthers(me)) return true;
            return false;
        }

        public static PlayerController NearestLivingOther(PlayerController me)
        {
            PlayerController best = null;
            float bestD = float.MaxValue;
            Vector2 at = me ? (Vector2)me.CenterPosition : Vector2.zero;
            foreach (var p in LivingOthers(me))
            {
                float d = ((Vector2)p.CenterPosition - at).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        public static List<PlayerController> Ghosts()
        {
            var list = new List<PlayerController>();
            foreach (var p in All) if (p.IsGhost) list.Add(p);
            return list;
        }

        /// <summary>A living player to stand in for "the other player": the nearest to <paramref name="me"/>, else anyone living.</summary>
        public static PlayerController LivingStandIn(PlayerController me)
        {
            var near = NearestLivingOther(me);
            if (near) return near;
            foreach (var p in All) if (IsLiving(p)) return p;
            return null;
        }
    }
}
