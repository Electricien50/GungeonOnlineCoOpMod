using System.IO;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>Harness heartbeat and timed quit. Inert for players.</summary>
    internal sealed class DevRunner : MonoBehaviour
    {
        float nextStatus;

        void Update()
        {
            if (!DevEnv.IsHarness) return;
            float t = Time.realtimeSinceStartup;
            if (DevEnv.StatusFile != null && t >= nextStatus)
            {
                nextStatus = t + 1f;
                var gm = GameManager.HasInstance ? GameManager.Instance : null;
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                int players = gm != null && gm.AllPlayers != null ? gm.AllPlayers.Length : 0;
                string line = $"t={t:F1} frame={Time.frameCount} scene={scene} gm={gm != null} players={players}";
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
