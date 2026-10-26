using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// A player moved into a fight (the Classic pull, a late entry, a move the host orders) was walking, rolling or
    /// shooting somewhere else. Carried over, that input steers them through the fight they just landed in. On the
    /// player's own machine their input is ignored for a moment after the move: until they let go of everything
    /// (between the player's own settings PullLockMinSeconds and PullLockMaxSeconds). Their frames meanwhile show
    /// no input, so the other screens agree.
    /// </summary>
    internal static class PullLock
    {
        const string Reason = "gungeon online pull";
        static float MinSeconds => Session.NetConfig.PullLockMin.Value;
        static float MaxSeconds => Mathf.Max(MinSeconds, Session.NetConfig.PullLockMax.Value);
        static int token;
        static PlayerController locked;

        /// <summary>This machine's player is under the lock.</summary>
        public static bool Active => locked && locked == PlayerSlots.Local;

        /// <summary>Call right after a move: does nothing for another machine's player.</summary>
        public static void Begin(PlayerController p)
        {
            if (!Session.NetSession.Active || !p || p != PlayerSlots.Local || MaxSeconds <= 0f) return;
            try { if (p.IsDodgeRolling) p.ForceStopDodgeRoll(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("PullLock: roll: " + e.Message); }
            if (locked != p) { p.SetInputOverride(Reason); locked = p; }
            p.StartCoroutine(Release(p, ++token));
        }

        static System.Collections.IEnumerator Release(PlayerController p, int mine)
        {
            float start = Time.realtimeSinceStartup;
            while (p && mine == token)
            {
                float t = Time.realtimeSinceStartup - start;
                if (t >= MaxSeconds || (t >= MinSeconds && !Holding(p))) break;
                yield return null;
            }
            if (mine != token) yield break; // a newer move restarted the lock
            if (p) p.ClearInputOverride(Reason);
            locked = null;
        }

        static bool Holding(PlayerController p)
        {
            var bi = BraveInput.GetInstanceForPlayer(p.PlayerIDX);
            var a = bi != null ? bi.ActiveActions : null;
            if (a == null) return false;
            return a.Move.Vector.sqrMagnitude > 0.04f || a.DodgeRollAction.IsPressed || a.ShootAction.IsPressed;
        }
    }
}
