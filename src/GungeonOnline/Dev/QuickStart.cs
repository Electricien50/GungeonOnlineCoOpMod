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
                // Do not wait on IsSelectingCharacter: at high frame rates it can flip before this loop sees it.
                if (Game.SceneObjects.InBreach && GameManager.HasInstance && GameManager.Instance.PrimaryPlayer == null)
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
            Plugin.Log.LogInfo("QuickStart: found flag, selecting shortly");
            yield return new WaitForSeconds(1f);
            if (GameManager.Instance.PrimaryPlayer != null) { Plugin.Log.LogInfo("QuickStart: player already chosen"); ScenarioRunner.Begin(); yield break; }
            Plugin.Log.LogInfo("QuickStart: selecting " + pick.CharacterPrefabPath);
            Game.SceneObjects.Breach.StartCoroutine(Game.SceneObjects.Breach.OnSelectedCharacter(0.05f, pick));
            while (GameManager.Instance.PrimaryPlayer == null) yield return null;
            Plugin.Log.LogInfo("QuickStart: player spawned at " + GameManager.Instance.PrimaryPlayer.CenterPosition);
            ScenarioRunner.Begin();
        }
    }
}
