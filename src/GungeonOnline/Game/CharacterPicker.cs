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
            SceneObjects.Breach.StartCoroutine(SceneObjects.Breach.OnSelectedCharacter(0.05f, pick));
            while (GameManager.Instance.PrimaryPlayer == null && Time.realtimeSinceStartup < giveUp + 10f) yield return null;
        }
    }
}
