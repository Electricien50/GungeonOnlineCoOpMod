using System;
using System.Collections;
using System.Collections.Generic;
using Dungeonator;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Replication;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>Opt-in integration checks in isolated game copies. Never active in ordinary play.</summary>
    internal sealed class ReleaseRegression : MonoBehaviour
    {
        static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("REGRESSION FAIL: " + message);
            Plugin.Log.LogInfo("REGRESSION PASS: " + message);
        }

        static void Kill(PlayerController p)
        {
            p.healthHaver.IsVulnerable = true;
            p.healthHaver.PreventAllDamage = false;
            p.healthHaver.Armor = 0f;
            p.healthHaver.ForceSetCurrentHealth(0.5f);
            p.healthHaver.ApplyDamage(1f, Vector2.zero, "regression", CoreDamageTypes.None, DamageCategory.Unstoppable, true);
        }

        IEnumerator Start()
        {
            string mode = Environment.GetEnvironmentVariable("GO_REGRESSION");
            if (!DevEnv.IsHarness || string.IsNullOrEmpty(mode)) yield break;
            while (!SceneObjects.OnDungeonFloor || PlayerSlots.Local == null ||
                (NetSession.IsHost ? WorldSync.Instance.ReadyPeers < DevEnv.ExpectPlayers - 1 : !WorldSync.Instance.FloorReady)) yield return null;
            yield return Wait(5f);
            var gm = GameManager.Instance;
            foreach (var p in Game.NPlayers.Players.All) p.healthHaver.IsVulnerable = false;
            if (mode == "rainbow")
                foreach (var item in PickupObjectDatabase.Instance.Objects) if (item) item.IgnoredByRat = true;
            if (mode == "shop")
            {
                if (NetSession.IsHost) yield break;
                var items = Traverse.Create(ShopSync.Instance).Field("clientItems").GetValue<Dictionary<uint, ShopItemController>>();
                Check(items.Count > 0, "shop stock mapped");
                uint id = 0; ShopItemController mine = null;
                foreach (var kv in items) if (kv.Value && !kv.Value.Acquired) { id = kv.Key; mine = kv.Value; break; }
                Check(mine, "unsold shop item found");
                int instance = mine.GetInstanceID();
                for (int i = 0; i < 3; i++)
                {
                    int item = mine.item.PickupObjectId == 15 ? 38 : 15;
                    var w = new WireWriter(32);
                    w.U32(id); w.I32(item); w.I32(10);
                    Vector2 at = mine.transform.position;
                    w.F32(at.x); w.F32(at.y); w.Bool(false);
                    byte[] data = w.ToArray(); var r = new WireReader(data, 0, data.Length);
                    ShopSync.Instance.Apply(null, 60, ref r);
                    yield return null; yield return null;
                    Check(mine && mine.GetInstanceID() == instance && mine.item.PickupObjectId == item &&
                        mine.GetComponents<tk2dSprite>().Length == 1 && mine.sprite.renderer.enabled,
                        "restock " + i + " keeps one visible sprite and the live controller");
                }
                PlayerSlots.Local.WarpToPoint((Vector2)mine.transform.position + new Vector2(0f, -2f));
                yield break;
            }
            if (!NetSession.IsHost)
            {
                if (mode == "rainbow")
                {
                    yield return Wait(18f);
                    LogLoot();
                    yield break;
                }
                if (mode == "jammed")
                {
                    yield return Wait(12f);
                    int jammed = 0, total = 0;
                    foreach (var e in StaticReferenceManager.AllEnemies)
                        if (e && e.GetComponent<PlayerController>() == null) { total++; if (e.IsBlackPhantom) jammed++; }
                    Plugin.Log.LogInfo("REGRESSION jammed guest " + jammed + "/" + total);
                    Check(total > 0 && jammed == total, "guest shows every host-jammed enemy as jammed");
                    Check(SuperReaperController.Instance, "guest sees Lord of the Jammed");
                    yield return Wait(4f);
                    Vector2 at = SuperReaperController.Instance.transform.position;
                    Plugin.Log.LogInfo("REGRESSION reaper guest at " + at);
                    yield break;
                }
                if (mode == "boss-revive")
                {
                    Kill(PlayerSlots.Local);
                    yield return Wait(80f);
                    Check(!PlayerSlots.Local.IsGhost && !PlayerSlots.Local.healthHaver.IsDead, "boss pedestal revives guest");
                    yield break;
                }
                if (mode.StartsWith("reset"))
                {
                    Kill(PlayerSlots.Local);
                    if (mode == "reset-guest-menu")
                    {
                        var end = Time.realtimeSinceStartup + 25f;
                        while (!UnityEngine.Object.FindObjectOfType<AmmonomiconDeathPageController>() && Time.realtimeSinceStartup < end) yield return null;
                        var page = UnityEngine.Object.FindObjectOfType<AmmonomiconDeathPageController>();
                        Check(page, "guest death screen appears");
                        AccessTools.Method(typeof(AmmonomiconDeathPageController), "DoMainMenu").Invoke(page, new object[] { null, null });
                        yield return Wait(1f);
                        Check(!gm.IsLoadingLevel && AmmonomiconController.Instance.IsOpen, "guest reset keeps the death screen open while waiting for host");
                    }
                    yield break;
                }
                if (mode == "chest")
                {
                    Kill(PlayerSlots.Local);
                    yield return Wait(5f);
                    Check((bool)AccessTools.Field(typeof(Chest), "m_IsCoopMode").GetValue(null), "guest sees revive chests");
                    yield return Wait(8f);
                    Check(!PlayerSlots.Local.IsGhost && !PlayerSlots.Local.healthHaver.IsDead, "guest revived from rainbow chest");
                }
                else if (mode.StartsWith("clone"))
                {
                    var floor = gm.Dungeon;
                    yield return Wait(mode == "clone-guest" ? 5f : 10f);
                    Kill(PlayerSlots.Local);
                    yield return Wait(30f);
                    Check(gm.Dungeon != floor && RunSync.CurrentIndex == 1, "Clone brings guest to chamber one");
                    foreach (var p in Game.NPlayers.Players.All)
                        Check(!p.IsGhost && !p.healthHaver.IsDead, "Clone revives slot " + PlayerSlots.SlotOf(p) + " on guest");
                }
                yield break;
            }
            PlayerController guest = null;
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) { guest = kv.Value; break; }
            Check(guest, "guest exists");
            if (mode == "pulldoor")
            {
                // The host stands in a doorway of a room, like walking into a fight; the guest is pulled to the host.
                var d = gm.Dungeon.data;
                IntVector2? door = null;
                foreach (var room in d.rooms)
                {
                    if (room.area == null || room == gm.Dungeon.data.Entrance) continue;
                    var b0 = room.area.basePosition; var dim = room.area.dimensions;
                    for (int x = b0.x - 1; x <= b0.x + dim.x && door == null; x++)
                        for (int y = b0.y - 1; y <= b0.y + dim.y && door == null; y++)
                            if (d.CheckInBounds(x, y) && d.cellData[x][y] != null && d.cellData[x][y].isExitCell && d.cellData[x][y].type == CellType.FLOOR && d.cellData[x][y].parentRoom == room) door = new IntVector2(x, y);
                    if (door != null) break;
                }
                Check(door != null, "found a doorway");
                PlayerSlots.Local.WarpToPoint(door.Value.ToVector2());
                yield return Wait(1.5f);
                Plugin.Log.LogInfo("REGRESSION host in doorway at " + door.Value + " room=" + (PlayerSlots.Local.CurrentRoom != null ? PlayerSlots.Local.CurrentRoom.GetRoomName() : "none"));
                guest.ReuniteWithOtherPlayer(PlayerSlots.Local);
                yield return Wait(4f);
                yield break;
            }
            if (mode == "jammed")
            {
                int n = 0;
                foreach (var e in new List<AIActor>(StaticReferenceManager.AllEnemies))
                    if (e && e.GetComponent<PlayerController>() == null) { e.BecomeBlackPhantom(); n++; }
                Plugin.Log.LogInfo("REGRESSION jammed host " + n);
                gm.Dungeon.SpawnCurseReaper();
                yield return Wait(1f);
                Check(SuperReaperController.Instance, "host spawned Lord of the Jammed");
                yield return Wait(15f);
                if (SuperReaperController.Instance) Plugin.Log.LogInfo("REGRESSION reaper host at " + (Vector2)SuperReaperController.Instance.transform.position);
                yield break;
            }
            if (mode == "rainbow")
            {
                var room = gm.Dungeon.data.rooms[gm.Dungeon.data.rooms.Count - 1];
                Vector2 at = room.GetCenteredVisibleClearSpot(2, 2).ToVector2();
                var chest = Chest.Spawn(gm.RewardManager.Rainbow_Chest, new Vector3(at.x, at.y, 0f), room, true);
                yield return Wait(3f);
                chest.ForceOpen(PlayerSlots.Local);
                yield return Wait(10f);
                LogLoot();
                yield break;
            }
            if (mode.StartsWith("reset"))
            {
                yield return Wait(6f); Kill(PlayerSlots.Local);
                var end = Time.realtimeSinceStartup + 25f;
                while (!UnityEngine.Object.FindObjectOfType<AmmonomiconDeathPageController>() && Time.realtimeSinceStartup < end) yield return null;
                var page = UnityEngine.Object.FindObjectOfType<AmmonomiconDeathPageController>();
                Check(page, "death screen appears");
                yield return Wait(5f);
                AccessTools.Method(typeof(AmmonomiconDeathPageController), mode == "reset-quick" ? "DoQuickRestart" : "DoMainMenu")
                    .Invoke(page, new object[] { null, null });
                Plugin.Log.LogInfo("REGRESSION: pressed death screen " + mode);
                yield break;
            }
            if (mode.StartsWith("clone"))
            {
                var owner = mode == "clone-host" ? PlayerSlots.Local : guest;
                ExtraLifeItem clone = null;
                foreach (var item in PickupObjectDatabase.Instance.Objects)
                    if (item is ExtraLifeItem extra && extra.extraLifeMode == ExtraLifeItem.ExtraLifeMode.CLONE) { clone = extra; break; }
                Check(clone, "Clone prefab exists");
                LootEngine.GivePrefabToPlayer(clone.gameObject, owner);
                yield return Wait(mode == "clone-host" ? 3f : 10f);
                Kill(PlayerSlots.Local);
                yield return Wait(30f);
                Check(!gm.IsPaused && !gm.IsLoadingLevel && RunSync.CurrentIndex == 1, "Clone finishes its shared load");
                foreach (var p in Game.NPlayers.Players.All)
                    Check(!p.IsGhost && !p.healthHaver.IsDead, "Clone revives slot " + PlayerSlots.SlotOf(p) + " on host");
                yield break;
            }
            if (mode == "chest")
            {
                RoomHandler room = null;
                foreach (var r in gm.Dungeon.data.rooms)
                    if (r != PlayerSlots.Local.CurrentRoom && r.area != null && r.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.REWARD) { room = r; break; }
                if (room == null) room = gm.Dungeon.data.rooms[gm.Dungeon.data.rooms.Count - 1];
                Vector2 at = room.GetCenteredVisibleClearSpot(2, 2).ToVector2();
                var chest = Chest.Spawn(gm.RewardManager.Rainbow_Chest, new Vector3(at.x, at.y, 0f), room, true);
                yield return Wait(8f);
                Check(guest.IsGhost, "guest is a ghost before chest opens");
                chest.Interact(PlayerSlots.Local);
                yield return Wait(4f);
                Check(!guest.IsGhost && !guest.healthHaver.IsDead, "host revives guest from rainbow chest");
                yield break;
            }
            if (mode.StartsWith("door"))
            {
                NetConfig.CoopStyle.Value = 1;
                RoomHandler fight = null; DungeonDoorController door = null; RoomHandler outside = null;
                foreach (var room in gm.Dungeon.data.rooms)
                {
                    var enemies = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear);
                    if (room.area == null || room.area.PrototypeRoomCategory != PrototypeDungeonRoom.RoomCategory.NORMAL ||
                        enemies == null || enemies.Count == 0 || room.connectedDoors == null) continue;
                    foreach (var d in room.connectedDoors)
                        if (d && !d.isLocked && (d.Mode == DungeonDoorController.DungeonDoorMode.COMPLEX || d.Mode == DungeonDoorController.DungeonDoorMode.SINGLE_DOOR))
                        {
                            var other = d.upstreamRoom == room ? d.downstreamRoom : d.upstreamRoom;
                            if (other != null) { fight = room; door = d; outside = other; break; }
                        }
                    if (fight != null) break;
                }
                Check(door, "normal combat door found");
                var late = mode == "door-host" ? PlayerSlots.Local : guest;
                var fighter = late == guest ? PlayerSlots.Local : guest;
                late.WarpToPoint(outside.GetCenteredVisibleClearSpot(2, 2).ToVector2());
                fighter.WarpToPoint(fight.GetCenteredVisibleClearSpot(2, 2).ToVector2());
                yield return Wait(3f);
                if (!fight.IsSealed) fight.SealRoom();
                Check(late.CurrentRoom != fight, "teammate starts outside sealed fight");
                AccessTools.Method(typeof(DungeonDoorController), "CheckForPlayerCollision").Invoke(door, new object[] { late.specRigidbody, Vector2.up });
                yield return Wait(2f);
                Check(late.CurrentRoom == fight && fight.IsSealed, "late teammate enters while fight stays sealed");
            }
        }

        static void LogLoot()
        {
            int count = 0;
            foreach (var d in StaticReferenceManager.AllDebris)
            {
                if (!d) continue;
                var p = d.GetComponentInChildren<PickupObject>();
                if (!p || p.IsBeingSold || p.PickupObjectId < 0 || (p is Gun gun && gun.CurrentOwner != null)) continue;
                count++;
                Vector2 at = p.transform.position;
                Plugin.Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "REGRESSION LOOT: {0} {1:F2} {2:F2}", p.PickupObjectId, at.x, at.y));
            }
            Check(count >= 8, "rainbow chest has at least eight ground items");
        }
    }
}
