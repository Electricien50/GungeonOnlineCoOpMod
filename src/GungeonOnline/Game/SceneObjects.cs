using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Scene objects of a type including inactive ones. Gungeon switches off rooms no player is near, and
    /// Unity's FindObjectsOfType skips inactive objects, so host and client would otherwise see different sets.
    /// </summary>
    internal static class SceneObjects
    {
        public static List<T> All<T>() where T : Component
        {
            var list = new List<T>();
            foreach (var o in Resources.FindObjectsOfTypeAll<T>())
                if (o != null && o.gameObject.scene.IsValid() && o.hideFlags == HideFlags.None) list.Add(o);
            return list;
        }

        /// <summary>True on a dungeon floor (not the Breach, where shops and drops belong to each player's own save).</summary>
        public static bool OnDungeonFloor =>
            GameManager.HasInstance && GameManager.Instance.Dungeon != null && !InBreach && !GameManager.Instance.IsLoadingLevel;

        static int breachFrame = -1;
        static Foyer breach;

        /// <summary>The Breach object, looked up once per frame (the game's getter searches the scene whenever it is absent).</summary>
        public static Foyer Breach
        {
            get
            {
                if (breachFrame != Time.frameCount) { breachFrame = Time.frameCount; breach = Foyer.Instance; }
                return breach;
            }
        }

        public static bool InBreach => Breach != null;
    }
}
