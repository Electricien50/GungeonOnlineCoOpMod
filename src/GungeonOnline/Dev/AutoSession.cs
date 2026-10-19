using GungeonOnline.Session;

namespace GungeonOnline.Dev
{
    /// <summary>Harness only: GO_AUTO=host or GO_AUTO=join:CODE starts a session at boot.</summary>
    internal static class AutoSession
    {
        public static void Begin()
        {
            var a = DevEnv.Auto;
            if (a == null || NetSession.Instance == null) return;
            if (a == "host") NetSession.Instance.Host();
            else if (a.StartsWith("join:")) NetSession.Instance.Join(a.Substring(5));
        }
    }
}
