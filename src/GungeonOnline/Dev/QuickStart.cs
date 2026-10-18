using System.Collections;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Harness only: skips the intro and main menu and picks a character in the Breach automatically.
    /// GO_CHAR selects the character prefab (default PlayerRogue = the Pilot).
    /// </summary>
    internal static class QuickStart
    {
        public static void Prepare()
        {
            if (!DevEnv.IsHarness) return;
            Foyer.DoIntroSequence = false;
            Foyer.DoMainMenu = false;
        }

        public static IEnumerator Run()
        {
            if (!DevEnv.IsHarness) yield break;
            string want = System.Environment.GetEnvironmentVariable("GO_CHAR");
            if (string.IsNullOrEmpty(want)) want = "PlayerRogue";
            float giveUp = Time.realtimeSinceStartup + 120f;
            FoyerCharacterSelectFlag pick = null;
            while (Time.realtimeSinceStartup < giveUp)
            {
                if (Foyer.Instance != null && GameManager.HasInstance && GameManager.Instance.IsSelectingCharacter &&
                    GameManager.Instance.PrimaryPlayer == null)
                {
                    foreach (var f in Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
                        if (!f.IsCoopCharacter && !f.IsAlternateCostume && f.CharacterPrefabPath != null &&
                            f.CharacterPrefabPath.EndsWith(want))
                            pick = f;
                    if (pick != null) break;
                }
                yield return null;
            }
            if (pick == null) { Plugin.Log.LogWarning("QuickStart: character flag not found: " + want); yield break; }
            yield return new WaitForSeconds(1f);
            Plugin.Log.LogInfo("QuickStart: selecting " + pick.CharacterPrefabPath);
            Foyer.Instance.StartCoroutine(Foyer.Instance.OnSelectedCharacter(0.05f, pick));
            while (GameManager.Instance.PrimaryPlayer == null) yield return null;
            Plugin.Log.LogInfo("QuickStart: player spawned at " + GameManager.Instance.PrimaryPlayer.CenterPosition);
            ScenarioRunner.Begin();
        }
    }
}
