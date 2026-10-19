using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Once per launch, asks GitHub for the latest release and shows one toast if it is newer than this build.
    /// One read-only request with nothing about the player in it; off with Settings, Help and advanced, Check for updates.
    /// </summary>
    internal static class UpdateCheck
    {
        const string Api = "https://api.github.com/repos/Hero88go/GungeonOnlineCoOpMod/releases/latest";
        const string Page = "github.com/Hero88go/GungeonOnlineCoOpMod";
        public static string Latest = "";

        public static IEnumerator Run()
        {
            // Tests never reach GitHub unless they point the check somewhere (GO_UPDATE_URL).
            string url = System.Environment.GetEnvironmentVariable("GO_UPDATE_URL");
            if (string.IsNullOrEmpty(url)) { if (Dev.DevEnv.IsHarness) yield break; url = Api; }
            if (!NetConfig.CheckUpdates.Value) yield break;
            yield return new WaitForSeconds(16f); // after the welcome toast
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("User-Agent", "GungeonOnlineCoOpMod");
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 10;
                yield return req.SendWebRequest();
                if (req.isNetworkError || req.isHttpError) { Plugin.Log.LogInfo("Update check: no answer (" + req.error + ")"); yield break; }
                var m = System.Text.RegularExpressions.Regex.Match(req.downloadHandler.text, @"""tag_name""\s*:\s*""v?([0-9][0-9.]*)");
                if (!m.Success) yield break;
                Latest = m.Groups[1].Value;
                Plugin.Log.LogInfo("Update check: latest " + Latest + ", this is " + Plugin.Version);
                if (Newer(Latest, Plugin.Version))
                    Toasts.Show("Update " + Latest + " available: " + Page, 12f);
            }
        }

        public static bool Newer(string a, string b)
        {
            var x = a.Split('.'); var y = b.Split('.');
            for (int i = 0; i < System.Math.Max(x.Length, y.Length); i++)
            {
                int p = i < x.Length && int.TryParse(x[i], out var t) ? t : 0;
                int q = i < y.Length && int.TryParse(y[i], out var u) ? u : 0;
                if (p != q) return p > q;
            }
            return false;
        }
    }
}
