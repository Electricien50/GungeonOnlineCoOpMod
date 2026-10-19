using System.IO;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>Harness heartbeat and timed quit. Inert for players.</summary>
    internal sealed class DevRunner : MonoBehaviour
    {
        float nextStatus;
        bool suspended;

        /// <summary>Test instances are always silent: Wwise volumes forced to 0 every frame, engine suspended once it is up.</summary>
        void Mute()
        {
            AudioListener.volume = 0f;
            try
            {
                AkSoundEngine.SetRTPCValue("VOL_MUS", 0f);
                AkSoundEngine.SetRTPCValue("VOL_SFX", 0f);
                AkSoundEngine.SetRTPCValue("VOL_UI", 0f);
                if (!suspended && AkSoundEngine.IsInitialized())
                {
                    suspended = AkSoundEngine.Suspend() == AKRESULT.AK_Success;
                    if (suspended) Plugin.Log.LogInfo("Harness: audio engine suspended (muted)");
                }
            }
            catch { }
        }

        void Awake()
        {
            if (DevEnv.IsHarness) Mute();
        }

        void Update()
        {
            if (!DevEnv.IsHarness) return;
            float t = Time.realtimeSinceStartup;
            Mute();
            if (DevEnv.StatusFile != null && t >= nextStatus)
            {
                nextStatus = t + 1f;
                var gm = GameManager.HasInstance ? GameManager.Instance : null;
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                int players = gm != null && gm.AllPlayers != null ? gm.AllPlayers.Length : 0;
                string pos = gm != null && gm.PrimaryPlayer != null ? gm.PrimaryPlayer.CenterPosition.ToString("F2") : "-";
                string net = "off";
                var s = Session.NetSession.Instance;
                if (Session.NetSession.Active)
                {
                    net = $"{(Session.NetSession.IsHost ? "host" : "client")} slot={Session.NetSession.LocalSlot} peers={s.Node.Connections.Count}";
                    foreach (var c in s.Node.Connections) net += $" [P{c.Slot + 1} rtt={c.RttMs:F1} loss={c.LossPercent:F1} sent={c.PacketsSent} recv={c.PacketsReceived}]";
                    if (s.InviteCodeText != null) net += " code=" + s.InviteCodeText;
                }
                string line = $"t={t:F1} frame={Time.frameCount} scene={scene} gm={gm != null} players={players} p0={pos} net={net}";
                try { File.WriteAllText(DevEnv.StatusFile, line); } catch { }
            }
            if (DevEnv.QuitAfter > 0 && t >= DevEnv.QuitAfter)
            {
                Plugin.Log.LogInfo("GO_QUIT_AFTER reached, quitting.");
                DevEnv.QuitAfter = -1;
                Application.Quit();
            }
        }
    }
}
