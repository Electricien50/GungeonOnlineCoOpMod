using System.Collections;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>Picks a character in the Breach the same way walking up to its statue would.</summary>
    internal static class CharacterPicker
    {
        public static IEnumerator Pick(string prefab)
        {
            float giveUp = Time.realtimeSinceStartup + 30f;
            FoyerCharacterSelectFlag pick = null;
            while (Time.realtimeSinceStartup < giveUp)
            {
                if (GameManager.HasInstance && GameManager.Instance.PrimaryPlayer != null) yield break;
                if (SceneObjects.InBreach)
                {
                    foreach (var f in Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
                        if (!f.IsCoopCharacter && !f.IsAlternateCostume && f.CharacterPrefabPath != null && f.CharacterPrefabPath.EndsWith(prefab))
                            pick = f;
                    if (pick != null) break;
                }
                yield return null;
            }
            if (pick == null) { Plugin.Log.LogWarning("CharacterPicker: no statue for " + prefab); yield break; }
            yield return new WaitForSeconds(0.5f);
            if (GameManager.Instance.PrimaryPlayer != null) yield break;
            Plugin.Log.LogInfo("CharacterPicker: picking " + pick.CharacterPrefabPath);
            // The Paradox and Gunslinger statues cost Hegemony Credits each time. This pick only brings back the
            // character already paid for (rejoin, resumed run), so whatever the statue takes is given back.
            float creditsBefore = GameStatsManager.HasInstance ? GameStatsManager.Instance.GetPlayerStatValue(TrackedStats.META_CURRENCY) : 0f;
            if (!SceneObjects.Breach) yield break;
            SceneObjects.Breach.StartCoroutine(SceneObjects.Breach.OnSelectedCharacter(0.05f, pick));
            while (GameManager.Instance.PrimaryPlayer == null && Time.realtimeSinceStartup < giveUp + 10f) yield return null;
            yield return null;
            if (GameStatsManager.HasInstance)
            {
                float taken = creditsBefore - GameStatsManager.Instance.GetPlayerStatValue(TrackedStats.META_CURRENCY);
                if (taken > 0f)
                {
                    GameStatsManager.Instance.RegisterStatChange(TrackedStats.META_CURRENCY, taken);
                    Plugin.Log.LogInfo($"CharacterPicker: refunded {taken} credits for the re-pick");
                }
            }
        }
    }
}
