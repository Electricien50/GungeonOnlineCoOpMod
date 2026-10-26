using System.Collections.Generic;
using Dungeonator;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Host: rewards scale with the number of players in the session. A chest holds a set of contents per player,
    /// a cleared room drops its pickup once per player, and a floor boss leaves a reward pedestal per player.
    /// Everything spawns on the host and reaches the other machines like any other loot.
    /// </summary>
    internal static class LootPerPlayer
    {
        static readonly System.Reflection.MethodInfo generate = AccessTools.Method(typeof(Chest), "GenerateContents");
        static readonly System.Reflection.FieldInfo chestRandom = AccessTools.Field(typeof(Chest), "m_runtimeRandom");
        static readonly HashSet<int> filledChests = new HashSet<int>();
        static bool roomReward, spawningExtra;
        static readonly System.Random roll = new System.Random(); // ours, so the game's own random sequence is untouched

        public static void Install(Harmony h)
        {
            var t = typeof(LootPerPlayer);
            h.Patch(AccessTools.Method(typeof(Chest), "DetermineContents"), postfix: new HarmonyMethod(t, nameof(ChestContentsPostfix)));
            h.Patch(AccessTools.Method(typeof(RoomHandler), nameof(RoomHandler.HandleRoomClearReward)),
                prefix: new HarmonyMethod(t, nameof(RoomRewardPrefix)), finalizer: new HarmonyMethod(t, nameof(RoomRewardFinalizer)));
            h.Patch(AccessTools.Method(typeof(LootEngine), nameof(LootEngine.SpawnItem),
                    new[] { typeof(GameObject), typeof(Vector3), typeof(Vector2), typeof(float), typeof(bool), typeof(bool), typeof(bool) }),
                postfix: new HarmonyMethod(t, nameof(SpawnItemPostfix)));
            h.Patch(AccessTools.Method(typeof(RoomHandler), "HandleBossClearReward"),
                prefix: new HarmonyMethod(t, nameof(BossRewardPrefix)), postfix: new HarmonyMethod(t, nameof(BossRewardPostfix)));
            h.Patch(AccessTools.Method(typeof(RewardPedestal), "DetermineContents"), prefix: new HarmonyMethod(t, nameof(PedestalContentsPrefix)));
        }

        public static readonly string[] ModeNames = { "Off", "Half", "One per player" };

        /// <summary>
        /// How many sets of rewards to give this time. Full: one per player in the session. Half: each player after
        /// the first adds half a set, a leftover half is a coin flip. Off, or not hosting: 1.
        /// </summary>
        static int Sets
        {
            get
            {
                if (!NetSession.Active || !NetSession.IsHost) return 1;
                int mode = NetConfig.LootPerPlayer.Value, extraPlayers = Mathf.Max(1, NPlayers.Players.Count) - 1;
                if (mode <= 0 || extraPlayers <= 0) return 1;
                if (mode >= 2) return 1 + extraPlayers;
                return 1 + extraPlayers / 2 + (extraPlayers % 2 == 1 && roll.NextDouble() < 0.5 ? 1 : 0);
            }
        }

        /// <summary>Guns and items (not hearts, ammo, keys...), which nobody wants twice.</summary>
        static bool Unique(PickupObject p) => p is Gun || p is PassiveItem || p is PlayerItem;

        // ---------------- Chests ----------------

        static void ChestContentsPostfix(Chest __instance, PlayerController player, int tierShift)
        {
            int sets = Sets;
            if (sets <= 1 || __instance.IsGlitched || __instance.contents == null || __instance.contents.Count == 0) return;
            // Chests that hold one particular thing (story items, quest notes) keep it as it is.
            if (__instance.forceContentIds != null && __instance.forceContentIds.Count > 0) return;
            if (!filledChests.Add(__instance.GetInstanceID())) return; // the game asks again when it opens
            var random = chestRandom != null ? chestRandom.GetValue(__instance) as System.Random : null;
            int before = __instance.contents.Count;
            var have = new HashSet<int>();
            foreach (var p in __instance.contents) if (p && Unique(p)) have.Add(p.PickupObjectId);
            for (int i = 1; i < sets; i++)
            {
                try
                {
                    // A set that repeats a gun or item already in the chest is drawn again (a few times at most).
                    List<PickupObject> pick = null;
                    for (int tries = 0; tries < 8; tries++)
                    {
                        var more = generate.Invoke(__instance, new object[] { player, tierShift, random }) as List<PickupObject>;
                        if (more == null) continue;
                        pick = more;
                        bool repeat = false;
                        foreach (var p in more) if (p && Unique(p) && have.Contains(p.PickupObjectId)) repeat = true;
                        if (!repeat) break;
                    }
                    if (pick == null) continue;
                    foreach (var p in pick)
                    {
                        if (!p) continue;
                        __instance.contents.Add(p);
                        if (Unique(p)) have.Add(p.PickupObjectId);
                    }
                }
                catch (System.Exception e) { Plugin.Log.LogWarning("LootPerPlayer: chest contents: " + e.Message); break; }
            }
            Plugin.Log.LogInfo($"LootPerPlayer: {__instance.name} holds {__instance.contents.Count} (was {before})");
        }

        // ---------------- Room clear ----------------

        static void RoomRewardPrefix() => roomReward = true;

        static System.Exception RoomRewardFinalizer(System.Exception __exception)
        {
            roomReward = false;
            return __exception;
        }

        static void SpawnItemPostfix(GameObject item, Vector3 spawnPosition, Vector2 spawnDirection, float force, bool invalidUntilGrounded, bool doDefaultItemPoof, bool disableHeightBoost)
        {
            if (!roomReward || spawningExtra || !item) return;
            int sets = Sets;
            if (sets <= 1) return;
            spawningExtra = true;
            try
            {
                for (int i = 1; i < sets; i++)
                {
                    // Side by side, so the drops do not land in one heap.
                    var offset = new Vector3((i % 2 == 1 ? 1f : -1f) * ((i + 1) / 2) * 0.9f, 0f, 0f);
                    LootEngine.SpawnItem(item, spawnPosition + offset, spawnDirection, force, invalidUntilGrounded, doDefaultItemPoof, disableHeightBoost);
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("LootPerPlayer: room reward: " + e.Message); }
            finally { spawningExtra = false; }
        }

        // ---------------- Floor boss ----------------

        sealed class BossState { public HashSet<RewardPedestal> Before; public int Sets; }

        static void BossRewardPrefix(out BossState __state)
        {
            int sets = Sets;
            __state = sets > 1 ? new BossState { Before = new HashSet<RewardPedestal>(Object.FindObjectsOfType<RewardPedestal>()), Sets = sets } : null;
        }

        static void BossRewardPostfix(RoomHandler __instance, BossState __state)
        {
            if (__state == null) return;
            int sets = __state.Sets;
            foreach (var rp in Object.FindObjectsOfType<RewardPedestal>())
            {
                if (!rp || __state.Before.Contains(rp) || !rp.IsBossRewardPedestal) continue;
                var prefab = PedestalPrefab(rp.name.Replace("(Clone)", "").Trim());
                if (prefab == null) { Plugin.Log.LogWarning("LootPerPlayer: no prefab for " + rp.name); continue; }
                var at = rp.transform.position.IntXY(VectorConversions.Floor);
                for (int i = 1; i < sets; i++)
                {
                    var spot = FreePedestalSpot(__instance, at);
                    try
                    {
                        var extra = RewardPedestal.Spawn(prefab, spot, __instance);
                        if (!extra) continue;
                        extra.IsBossRewardPedestal = true;
                        if (extra.lootTable != null && rp.lootTable != null) extra.lootTable.lootTable = rp.lootTable.lootTable;
                        extra.RegisterChestOnMinimap(__instance);
                        Occupy(spot);
                    }
                    catch (System.Exception e) { Plugin.Log.LogWarning("LootPerPlayer: boss pedestal: " + e.Message); }
                }
            }
        }

        /// <summary>
        /// Host: a boss pedestal picks its gun or item when it appears. Several in one room must not show the same one,
        /// so draw it here the way the game does, and again while another pedestal of that room already shows it.
        /// Seeded runs, rainbow runs and pedestals that hold one set thing are left to the game.
        /// </summary>
        static void PedestalContentsPrefix(RewardPedestal __instance, PlayerController player)
        {
            if (!NetSession.Active || !NetSession.IsHost || !__instance.IsBossRewardPedestal || __instance.contents != null) return;
            if (GameStatsManager.Instance.IsRainbowRun || GameManager.Instance.IsSeeded) return;
            var room = __instance.transform.position.GetAbsoluteRoom();
            var taken = new HashSet<int>();
            foreach (var other in Object.FindObjectsOfType<RewardPedestal>())
                if (other && other != __instance && other.IsBossRewardPedestal && other.contents && other.transform.position.GetAbsoluteRoom() == room)
                    taken.Add(other.contents.PickupObjectId);
            if (taken.Count == 0) return; // alone so far: the game draws as usual
            try
            {
                PickupObject pick = null;
                for (int tries = 0; tries < 10; tries++)
                {
                    GameObject go = __instance.lootTable != null && __instance.lootTable.lootTable != null
                        ? __instance.lootTable.lootTable.SelectByWeightWithoutDuplicatesFullPrereqs(null)
                        : GameManager.Instance.RewardManager.GetRewardObjectBossStyle(player);
                    var p = go ? go.GetComponent<PickupObject>() : null;
                    if (!p) continue;
                    pick = p;
                    if (!taken.Contains(p.PickupObjectId)) break;
                }
                if (pick) __instance.contents = pick;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("LootPerPlayer: pedestal contents: " + e.Message); }
        }

        static RewardPedestal PedestalPrefab(string name)
        {
            foreach (var rp in Resources.FindObjectsOfTypeAll<RewardPedestal>())
                if (rp && !rp.gameObject.scene.IsValid() && rp.name == name) return rp;
            return null;
        }

        /// <summary>The nearest free 2 by 2 floor spot beside the first pedestal, in steps of three cells.</summary>
        static IntVector2 FreePedestalSpot(RoomHandler room, IntVector2 at)
        {
            var d = GameManager.Instance.Dungeon.data;
            for (int r = 1; r <= 4; r++)
                foreach (var dir in new[] { new IntVector2(1, 0), new IntVector2(-1, 0), new IntVector2(0, -1), new IntVector2(1, -1), new IntVector2(-1, -1), new IntVector2(0, 1) })
                {
                    var p = at + new IntVector2(dir.x * 3 * r, dir.y * 3 * r);
                    bool ok = true;
                    for (int x = 0; x < 2 && ok; x++)
                        for (int y = 0; y < 2 && ok; y++)
                        {
                            var c = d.CheckInBounds(p.x + x, p.y + y) ? d.cellData[p.x + x][p.y + y] : null;
                            ok = c != null && c.type == CellType.FLOOR && !c.isOccupied && c.parentRoom == room && !c.isExitCell && !c.doesDamage;
                        }
                    if (ok) return p;
                }
            return room.GetCenteredVisibleClearSpot(2, 2);
        }

        static void Occupy(IntVector2 p)
        {
            var d = GameManager.Instance.Dungeon.data;
            for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                    if (d.CheckInBounds(p.x + x, p.y + y) && d.cellData[p.x + x][p.y + y] != null) d.cellData[p.x + x][p.y + y].isOccupied = true;
        }
    }
}
