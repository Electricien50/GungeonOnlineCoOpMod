using GungeonOnline.Session;

namespace GungeonOnline.Dev
{
    /// <summary>Harness only: GO_AUTO=host or GO_AUTO=join:CODE starts a session at boot.</summary>
    internal static class AutoSession
    {
        static System.Collections.IEnumerator Later(System.Action a)
        {
            yield return new UnityEngine.WaitForSeconds(3f);
            a();
        }

        /// <summary>Harness: after a host crash, resume the saved run and leave the Breach once the friend is back.</summary>
        static System.Collections.IEnumerator Resume()
        {
            while (!Game.SceneObjects.InBreach || GameManager.Instance.PrimaryPlayer == null) yield return null;
            yield return new UnityEngine.WaitForSeconds(2f);
            RunBackup.ResumeAsHost();
            float giveUp = UnityEngine.Time.realtimeSinceStartup + 60f;
            while (Game.PlayerSlots.Remotes.GetEnumerator().MoveNext() == false && UnityEngine.Time.realtimeSinceStartup < giveUp) yield return null;
            yield return new UnityEngine.WaitForSeconds(3f);
            UnityEngine.Debug.Log("Harness: resuming run, leaving the Breach");
            GameManager.Instance.DelayedLoadNextLevel(0.5f);
            Game.SceneObjects.Breach.OnDepartedFoyer();
        }

        /// <summary>Harness: wait for the host instance to write its Steam id, then join it over Steam.</summary>
        static System.Collections.IEnumerator SteamJoin(string file)
        {
            float giveUp = UnityEngine.Time.realtimeSinceStartup + 90f;
            while (UnityEngine.Time.realtimeSinceStartup < giveUp && !System.IO.File.Exists(file)) yield return null;
            yield return new UnityEngine.WaitForSeconds(2f);
            ulong id;
            if (!System.IO.File.Exists(file) || !ulong.TryParse(System.IO.File.ReadAllText(file).Trim(), out id)) { UnityEngine.Debug.Log("Harness: no Steam host id"); yield break; }
            UnityEngine.Debug.Log("Harness: joining over Steam " + id);
            NetSession.Instance.JoinSteam(new Steamworks.CSteamID(id));
        }

        public static void Begin()
        {
            var a = DevEnv.Auto;
            if (a == null || NetSession.Instance == null) return;
            if (a == "host") NetSession.Instance.Host();
            else if (a == "rejoin") NetSession.Instance.StartCoroutine(Later(() => NetSession.Instance.RejoinLast()));
            else if (a == "resume") NetSession.Instance.StartCoroutine(Resume());
            else if (a.StartsWith("join:")) NetSession.Instance.Join(a.Substring(5));
            else if (a.StartsWith("steamjoinfile:")) NetSession.Instance.StartCoroutine(SteamJoin(a.Substring(14)));
        }
    }
}
