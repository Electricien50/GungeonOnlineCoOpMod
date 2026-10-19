using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// The one place where network slots meet the game's player numbers.
    /// On every machine the local player is the game's player 0 (PrimaryPlayer), so each screen gets its own
    /// HUD on the left, its own audio listener and its own camera. Remote players are added the way vanilla
    /// co-op adds player 2, with any character. The game supports one extra player today; the slot map is N-ready.
    /// </summary>
    internal static class PlayerSlots
    {
        /// <summary>slot -> spawned remote player (local slot is not in here).</summary>
        static readonly Dictionary<int, PlayerController> remotes = new Dictionary<int, PlayerController>();
        /// <summary>slot -> prefab path the remote player chose.</summary>
        static readonly Dictionary<int, string> remoteChoice = new Dictionary<int, string>();

        public static int LocalSlot => Session.NetSession.Active ? Session.NetSession.LocalSlot : 0;

        public static PlayerController Local => GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;

        public static IEnumerable<KeyValuePair<int, PlayerController>> Remotes => remotes;

        /// <summary>Player for a network slot on this machine, or null.</summary>
        public static PlayerController ForSlot(int slot)
        {
            if (slot == LocalSlot) return Local;
            return remotes.TryGetValue(slot, out var p) && p ? p : null;
        }

        /// <summary>The game's player id for a slot here (0 for local, 1.. for remotes).</summary>
        public static int PlayerIdFor(int slot)
        {
            var p = ForSlot(slot);
            return p ? p.PlayerIDX : -1;
        }

        public static int SlotOf(PlayerController p)
        {
            if (p == Local) return LocalSlot;
            foreach (var kv in remotes) if (kv.Value == p) return kv.Key;
            return -1;
        }

        public static bool IsRemote(PlayerController p) => p != null && p != Local && SlotOf(p) >= 0;

        /// <summary>Spawn (or re-spawn with a new character) the remote player for <paramref name="slot"/>.</summary>
        public static PlayerController SpawnRemote(int slot, string prefabPath)
        {
            var gm = GameManager.Instance;
            var local = Local;
            if (local == null) return null; // spawn once the local player exists
            if (remotes.TryGetValue(slot, out var existing) && existing)
            {
                if (remoteChoice.TryGetValue(slot, out var prev) && prev == prefabPath) return existing;
                Despawn(slot);
            }
            var prefab = (GameObject)BraveResources.Load(prefabPath);
            if (prefab == null) { Plugin.Log.LogError("Unknown character prefab " + prefabPath); return null; }

            gm.CurrentGameType = GameManager.GameType.COOP_2_PLAYER;
            local.ReinitializeMovementRestrictors();
            gm.ClearSecondaryPlayer();
            GameManager.LastUsedCoopPlayerPrefab = prefab;
            var go = Object.Instantiate(prefab, local.transform.position + new Vector3(1.5f, 0f, 0f), Quaternion.identity);
            go.SetActive(true);
            var p = go.GetComponent<PlayerController>();
            p.PlayerIDX = 1;
            gm.SecondaryPlayer = p;
            remotes[slot] = p;
            remoteChoice[slot] = prefabPath;
            Plugin.Instance.StartCoroutine(FinishSpawn(p));
            Plugin.Log.LogInfo($"PlayerSlots: slot {slot} spawned as {prefabPath} (player id {p.PlayerIDX})");
            return p;
        }

        static System.Collections.IEnumerator FinishSpawn(PlayerController p)
        {
            yield return null;
            if (!p) yield break;
            GameUIRoot.Instance.ConvertCoreUIToCoopMode();
            PhysicsEngine.Instance.RegisterOverlappingGhostCollisionExceptions(p.specRigidbody);
            GameManager.Instance.MainCameraController.ClearPlayerCache();
            if (Game.SceneObjects.InBreach) Game.SceneObjects.Breach.ProcessPlayerEnteredFoyer(p);
            // Vanilla co-op restores input when the join conversation ends; there is no conversation here.
            p.CurrentInputState = PlayerInputState.AllInput;
            RefreshFoyerForCoopChange();
            Camera.IndependentCamera.Apply();
        }

        public static void Despawn(int slot)
        {
            if (!remotes.TryGetValue(slot, out var p)) return;
            remotes.Remove(slot);
            remoteChoice.Remove(slot);
            Input.InputPump.Unroute(p ? p.PlayerIDX : 1);
            if (p)
            {
                p.SetInputOverride("gungeon online leaving");
                Object.Destroy(p.gameObject);
            }
            if (remotes.Count == 0 && GameManager.HasInstance)
            {
                GameManager.Instance.SecondaryPlayer = null;
                GameManager.Instance.CurrentGameType = GameManager.GameType.SINGLE_PLAYER;
                if (Local) Local.ReinitializeMovementRestrictors();
            }
            RefreshFoyerForCoopChange();
            Plugin.Log.LogInfo($"PlayerSlots: slot {slot} removed");
        }

        /// <summary>
        /// Breach objects that depend on co-op mode (the tutorial blocker) are only updated by vanilla when a
        /// character changes. Re-run that rule so host and client Breaches have the same walls.
        /// </summary>
        static void RefreshFoyerForCoopChange()
        {
            if (!Game.SceneObjects.InBreach) return;
            try { AccessTools.Method(typeof(Foyer), "ToggleTutorialBlocker").Invoke(Foyer.Instance, new object[] { Local }); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Foyer refresh: " + e.Message); }
        }

        public static void Clear()
        {
            foreach (var s in new List<int>(remotes.Keys)) Despawn(s);
        }

        static readonly AccessTools.FieldRef<PlayerController, bool> ignoredByCamera =
            AccessTools.FieldRefAccess<PlayerController, bool>("<IgnoredByCamera>k__BackingField");

        public static void SetIgnoredByCamera(PlayerController p, bool v) => ignoredByCamera(p) = v;
    }
}
