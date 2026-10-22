using System;
using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Items on the ground and chests are host-owned, because what drops depends on the host's save.
    /// Host: announces every pickup lying in the world and every chest; whenever any player picks something up
    /// it tells everyone, and every machine gives the same item to its copy of that player (so inventories match
    /// everywhere and your own character really holds the gun it shoots).
    /// Client: shows copies, removes anything it generated on its own, and asks the host to pick up or open.
    /// </summary>
    [DefaultExecutionOrder(31800)]
    internal sealed class PickupSync : MonoBehaviour
    {
        public static PickupSync Instance { get; private set; }
        const byte EvPickup = 50, EvPickupGone = 51, EvPickupReq = 52, EvGot = 53, EvChest = 54, EvChestReq = 55, EvChestOpened = 56, EvChestGone = 57, EvPedestal = 58, EvPedestalReq = 59;

        static bool scope;

        /// <summary>Run game code that would otherwise be blocked on the client (applying host state).</summary>
        public static void WithScope(Action a)
        {
            bool prev = scope;
            scope = true;
            try { a(); } finally { scope = prev; }
        }
        static bool IsClient => NetSession.Active && !NetSession.IsHost;
        static bool IsHostSession => NetSession.Active && NetSession.IsHost;

        // Host
        readonly Dictionary<uint, PickupObject> hostPickups = new Dictionary<uint, PickupObject>();
        readonly Dictionary<uint, Chest> hostChests = new Dictionary<uint, Chest>();
        readonly Dictionary<uint, bool> chestOpenSent = new Dictionary<uint, bool>();
        uint nextId = 1;
        float nextScan;

        // Client
        readonly Dictionary<uint, PickupObject> copies = new Dictionary<uint, PickupObject>();
        static readonly Dictionary<int, uint> copyIdByInstance = new Dictionary<int, uint>();
        readonly Dictionary<uint, Chest> clientChests = new Dictionary<uint, Chest>();
        static readonly Dictionary<int, uint> chestIdByInstance = new Dictionary<int, uint>();
        readonly Dictionary<uint, float> lastRequest = new Dictionary<uint, float>();

        public int PickupsAnnounced, PickupsCopied, LocalPurged, Requests, Gives, GivesApplied, ChestsMapped, ChestsOpened;
        readonly WireWriter w = new WireWriter(128);

        public static void Install(Harmony h)
        {
            var pre = new HarmonyMethod(typeof(PickupSync), nameof(PickupPrefix));
            var post = new HarmonyMethod(typeof(PickupSync), nameof(PickupPostfix));
            int n = 0;
            foreach (var t in typeof(PickupObject).Assembly.GetTypes())
            {
                if (!typeof(PickupObject).IsAssignableFrom(t)) continue;
                var m = t.GetMethod("Pickup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly, null, new[] { typeof(PlayerController) }, null);
                if (m == null || m.IsAbstract) continue;
                h.Patch(m, prefix: pre, postfix: post);
                n++;
            }
            var c = typeof(Chest);
            h.Patch(AccessTools.Method(c, nameof(Chest.Interact)), prefix: new HarmonyMethod(typeof(PickupSync), nameof(ChestInteractPrefix)));
            foreach (var name in new[] { "ForceOpen", "Open", "MaybeBecomeMimic", "DoMimicTransformation", "ExplodeInSadness", "BreakLock" })
            {
                var m = AccessTools.Method(c, name);
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(PickupSync), nameof(ClientChestBlock)));
            }
            h.Patch(AccessTools.Method(c, "Open"), postfix: new HarmonyMethod(typeof(PickupSync), nameof(ChestOpenPostfix)));
            h.Patch(AccessTools.Method(typeof(RewardPedestal), nameof(RewardPedestal.Interact)), prefix: new HarmonyMethod(typeof(PickupSync), nameof(PedestalInteractPrefix)));
            Plugin.Log.LogInfo($"PickupSync: patched {n} pickup types");
        }

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.SessionEnded += () =>
            {
                hostPickups.Clear(); hostChests.Clear(); chestOpenSent.Clear(); hostPedestals.Clear(); pedestalPickedSent.Clear(); clientPedestals.Clear(); pedestalIdByInstance.Clear(); copies.Clear(); copyIdByInstance.Clear();
                clientChests.Clear(); chestIdByInstance.Clear();
            };
        }

        public void OnFloorChanged()
        {
            hostPickups.Clear(); hostChests.Clear(); chestOpenSent.Clear(); hostPedestals.Clear(); pedestalPickedSent.Clear(); clientPedestals.Clear(); pedestalIdByInstance.Clear(); hostIdByInstance.Clear(); pickupOfDebris.Clear(); lastRequest.Clear();
            copies.Clear(); copyIdByInstance.Clear(); clientChests.Clear(); chestIdByInstance.Clear();
        }

        // ---------------- Patches ----------------

        static bool PickupPrefix(PickupObject __instance, PlayerController player)
        {
            if (!NetSession.Active || scope) return true;
            if (IsClient)
            {
                // Local pickups never happen on the client; copies turn into a request to the host.
                if (copyIdByInstance.TryGetValue(__instance.GetInstanceID(), out var id) && player == PlayerSlots.Local)
                    Instance.Request(EvPickupReq, id);
                return false;
            }
            return true;
        }

        static void PickupPostfix(PickupObject __instance, PlayerController player)
        {
            if (!IsHostSession || scope || player == null || Instance == null) return;
            int slot = PlayerSlots.SlotOf(player);
            if (slot < 0) return;
            var w = Instance.w;
            w.Reset(); w.U8(EvGot); w.U8((byte)slot); w.I32(__instance.PickupObjectId);
            Instance.Send(w.ToArray());
            Instance.Gives++;
        }

        static bool ChestInteractPrefix(Chest __instance, PlayerController player)
        {
            if (!IsClient || scope) return true;
            if (chestIdByInstance.TryGetValue(__instance.GetInstanceID(), out var id) && player == PlayerSlots.Local)
                Instance.Request(EvChestReq, id);
            return false;
        }

        static bool ClientChestBlock() => !IsClient || scope;

        static void ChestOpenPostfix(Chest __instance)
        {
            if (!IsHostSession || Instance == null) return;
            foreach (var kv in Instance.hostChests)
                if (kv.Value == __instance && !Instance.chestOpenSent.ContainsKey(kv.Key))
                {
                    Instance.chestOpenSent[kv.Key] = true;
                    var w = Instance.w;
                    w.Reset(); w.U8(EvChestOpened); w.U32(kv.Key);
                    Instance.Send(w.ToArray());
                }
        }

        // ---------------- Boss reward pedestals ----------------
        // Only the host's game spawns them (the guest's copy of a boss dies without its room reward). The host
        // announces each one with its item; the guest builds the same pedestal holding the same item, and taking it
        // goes through the host like a chest, so the item arrives through the normal give.

        readonly Dictionary<uint, RewardPedestal> hostPedestals = new Dictionary<uint, RewardPedestal>();
        readonly Dictionary<uint, bool> pedestalPickedSent = new Dictionary<uint, bool>();
        readonly Dictionary<uint, RewardPedestal> clientPedestals = new Dictionary<uint, RewardPedestal>();
        static readonly Dictionary<int, uint> pedestalIdByInstance = new Dictionary<int, uint>();
        public int PedestalsShown;

        static bool PedestalInteractPrefix(RewardPedestal __instance, PlayerController player)
        {
            if (!IsClient || scope) return true;
            if (pedestalIdByInstance.TryGetValue(__instance.GetInstanceID(), out var id) && player == PlayerSlots.Local && !__instance.pickedUp)
                Instance.Request(EvPedestalReq, id);
            return false;
        }

        byte[] PedestalMsg(uint id, RewardPedestal rp)
        {
            var ww = new WireWriter(64);
            ww.U8(EvPedestal); ww.U32(id); ww.Str(PrefabName(rp.name));
            Vector2 pos = rp.transform.position;
            ww.F32(pos.x); ww.F32(pos.y);
            ww.I32(rp.contents ? rp.contents.PickupObjectId : -1);
            ww.Bool(rp.pickedUp);
            return ww.ToArray();
        }

        void ScanPedestals()
        {
            foreach (var rp in FindObjectsOfType<RewardPedestal>())
            {
                if (!rp || rp.contents == null) continue; // contents are chosen a moment after it appears
                uint id = 0;
                foreach (var kv in hostPedestals) if (kv.Value == rp) { id = kv.Key; break; }
                if (id == 0) { id = nextId++; hostPedestals[id] = rp; pedestalPickedSent[id] = rp.pickedUp; Send(PedestalMsg(id, rp)); continue; }
                if (pedestalPickedSent[id] != rp.pickedUp) { pedestalPickedSent[id] = rp.pickedUp; Send(PedestalMsg(id, rp)); }
            }
            foreach (var id in new List<uint>(hostPedestals.Keys))
                if (!hostPedestals[id]) { hostPedestals.Remove(id); pedestalPickedSent.Remove(id); }
        }

        static RewardPedestal PedestalPrefab(string name)
        {
            foreach (var rp in Resources.FindObjectsOfTypeAll<RewardPedestal>())
                if (rp && !rp.gameObject.scene.IsValid() && rp.name == name) return rp;
            return null;
        }

        void ShowPedestal(uint id, string name, Vector2 pos, int itemId, bool picked)
        {
            if (clientPedestals.TryGetValue(id, out var have) && have)
            {
                if (picked && !have.pickedUp) HidePedestalItem(have);
                return;
            }
            var prefab = PedestalPrefab(name);
            var item = itemId >= 0 ? PickupObjectDatabase.GetById(itemId) : null;
            if (prefab == null || item == null) { Plugin.Log.LogWarning($"PickupSync: cannot show pedestal {name} item {itemId}"); return; }
            var room = GameManager.Instance.Dungeon ? GameManager.Instance.Dungeon.data.GetAbsoluteRoomFromPosition(pos.ToIntVector2(VectorConversions.Floor)) : null;
            // Built from the prefab with the host's item; never a mimic, and its landing never revives anyone here.
            bool specific = prefab.UsesSpecificItem; int specificId = prefab.SpecificItemId; float mimic = prefab.overrideMimicChance; bool coop = prefab.ReturnCoopPlayerOnLand;
            prefab.UsesSpecificItem = true; prefab.SpecificItemId = itemId; prefab.overrideMimicChance = 0f; prefab.ReturnCoopPlayerOnLand = false;
            RewardPedestal rp;
            try { rp = RewardPedestal.Spawn(prefab, pos.ToIntVector2(VectorConversions.Floor), room); }
            finally { prefab.UsesSpecificItem = specific; prefab.SpecificItemId = specificId; prefab.overrideMimicChance = mimic; prefab.ReturnCoopPlayerOnLand = coop; }
            if (!rp) return;
            rp.transform.position = new Vector3(pos.x, pos.y, rp.transform.position.z);
            if (rp.specRigidbody) rp.specRigidbody.Reinitialize();
            clientPedestals[id] = rp;
            pedestalIdByInstance[rp.GetInstanceID()] = id;
            PedestalsShown++;
            if (picked) HidePedestalItem(rp);
        }

        static readonly System.Reflection.FieldInfo itemDisplay = AccessTools.Field(typeof(RewardPedestal), "m_itemDisplaySprite");

        static void HidePedestalItem(RewardPedestal rp)
        {
            rp.pickedUp = true;
            var spr = itemDisplay?.GetValue(rp) as tk2dBaseSprite;
            if (spr) Destroy(spr.gameObject);
        }

        // ---------------- Host ----------------

        void Send(byte[] data)
        {
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, data, Delivery.ReliableOrdered);
        }

        void Request(byte type, uint id)
        {
            float now = Time.realtimeSinceStartup;
            uint key = id ^ ((uint)type << 24);
            if (lastRequest.TryGetValue(key, out var t) && now - t < 0.3f) return;
            lastRequest[key] = now;
            w.Reset(); w.U8(type); w.U32(id);
            NetSession.Instance.Node.Broadcast((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
            NetSession.Instance.FlushNow();
            Requests++;
        }

        static bool InWorld(PickupObject p) =>
            p != null && p.gameObject.activeInHierarchy && p.transform.parent == null && !p.IsBeingSold && p.PickupObjectId >= 0;

        byte[] PickupMsg(uint id, PickupObject p)
        {
            var ww = new WireWriter(32);
            ww.U8(EvPickup); ww.U32(id); ww.I32(p.PickupObjectId);
            Vector2 pos = p.transform.position;
            ww.F32(pos.x); ww.F32(pos.y);
            return ww.ToArray();
        }

        byte[] ChestMsg(uint id, Chest ch)
        {
            var ww = new WireWriter(64);
            ww.U8(EvChest); ww.U32(id); ww.Str(PrefabName(ch.name));
            Vector2 pos = ch.transform.position;
            ww.F32(pos.x); ww.F32(pos.y); ww.Bool(ch.IsOpen);
            return ww.ToArray();
        }

        static string PrefabName(string n)
        {
            int i = n.IndexOf("(Clone)");
            return (i >= 0 ? n.Substring(0, i) : n).Trim();
        }

        static readonly Dictionary<int, PickupObject> pickupOfDebris = new Dictionary<int, PickupObject>();

        /// <summary>Items lying in the world. Dropped items are debris objects, which the game keeps in a list,
        /// so this avoids searching the whole scene.</summary>
        static IEnumerable<PickupObject> WorldPickups()
        {
            foreach (var d in StaticReferenceManager.AllDebris)
            {
                if (!d) continue;
                int k = d.GetInstanceID();
                if (!pickupOfDebris.TryGetValue(k, out var p)) pickupOfDebris[k] = p = d.GetComponent<PickupObject>();
                if (p) yield return p;
            }
        }

        readonly Dictionary<int, uint> hostIdByInstance = new Dictionary<int, uint>();

        float nextPedestalScan;

        void HostScan()
        {
            // Pedestals are rare and not in any game list: look for them twice a second, not every frame.
            if (Time.realtimeSinceStartup >= nextPedestalScan) { nextPedestalScan = Time.realtimeSinceStartup + 0.5f; ScanPedestals(); }
            var seen = new HashSet<uint>();
            foreach (var p in WorldPickups())
            {
                if (!InWorld(p)) continue;
                if (!hostIdByInstance.TryGetValue(p.GetInstanceID(), out var id))
                {
                    id = nextId++;
                    hostPickups[id] = p;
                    hostIdByInstance[p.GetInstanceID()] = id;
                    Send(PickupMsg(id, p));
                    PickupsAnnounced++;
                }
                seen.Add(id);
            }
            foreach (var id in new List<uint>(hostPickups.Keys))
            {
                if (seen.Contains(id)) continue;
                if (hostPickups[id]) hostIdByInstance.Remove(hostPickups[id].GetInstanceID());
                hostPickups.Remove(id);
                w.Reset(); w.U8(EvPickupGone); w.U32(id);
                Send(w.ToArray());
            }
            foreach (var ch in StaticReferenceManager.AllChests)
            {
                if (!ch) continue;
                if (!hostChests.ContainsValue(ch))
                {
                    uint id = nextId++;
                    hostChests[id] = ch;
                    Send(ChestMsg(id, ch));
                }
            }
            foreach (var id in new List<uint>(hostChests.Keys))
            {
                if (hostChests[id]) continue;
                hostChests.Remove(id);
                w.Reset(); w.U8(EvChestGone); w.U32(id);
                Send(w.ToArray());
            }
        }

        /// <summary>Host: a client became ready; send it everything on the ground and every chest.</summary>
        public void SendAllTo(Connection c)
        {
            foreach (var kv in hostPickups) if (kv.Value) c.Send((byte)Channel.Event, PickupMsg(kv.Key, kv.Value), Delivery.ReliableOrdered);
            foreach (var kv in hostChests) if (kv.Value) c.Send((byte)Channel.Event, ChestMsg(kv.Key, kv.Value), Delivery.ReliableOrdered);
            foreach (var kv in hostPedestals) if (kv.Value) c.Send((byte)Channel.Event, PedestalMsg(kv.Key, kv.Value), Delivery.ReliableOrdered);
        }

        // ---------------- Both: event router (types 50-59) ----------------

        public void Apply(Connection c, byte type, ref WireReader r)
        {
            scope = true;
            try
            {
                switch (type)
                {
                    case EvPickupReq when NetSession.IsHost:
                        {
                            uint id = r.U32();
                            var player = PlayerSlots.ForSlot(c.Slot);
                            if (hostPickups.TryGetValue(id, out var p) && p && InWorld(p) && player)
                            {
                                scope = false; // let the normal pickup path (and its broadcast) run
                                if (p is IPlayerInteractable ia) ia.Interact(player); else p.Pickup(player);
                            }
                            break;
                        }
                    case EvPedestalReq when NetSession.IsHost:
                        {
                            uint id = r.U32();
                            var player = PlayerSlots.ForSlot(c.Slot);
                            if (hostPedestals.TryGetValue(id, out var rp) && rp && !rp.pickedUp && player) { scope = false; rp.Interact(player); }
                            break;
                        }
                    case EvPedestal when !NetSession.IsHost:
                        {
                            uint id = r.U32();
                            string name = r.Str();
                            var pos = new Vector2(r.F32(), r.F32());
                            int itemId = r.I32();
                            bool picked = r.Bool();
                            ShowPedestal(id, name, pos, itemId, picked);
                            break;
                        }
                    case EvChestReq when NetSession.IsHost:
                        {
                            uint id = r.U32();
                            var player = PlayerSlots.ForSlot(c.Slot);
                            if (hostChests.TryGetValue(id, out var ch) && ch && player) { scope = false; ch.Interact(player); }
                            break;
                        }
                    case EvPickup when !NetSession.IsHost:
                        {
                            uint id = r.U32();
                            int itemId = r.I32();
                            var pos = new Vector2(r.F32(), r.F32());
                            if (copies.ContainsKey(id)) break;
                            var prefab = PickupObjectDatabase.GetById(itemId);
                            if (prefab == null) break;
                            var go = Instantiate(prefab.gameObject, new Vector3(pos.x, pos.y, 0f), Quaternion.identity);
                            var po = go.GetComponent<PickupObject>();
                            if (po == null) { Destroy(go); break; }
                            copies[id] = po;
                            copyIdByInstance[po.GetInstanceID()] = id;
                            PrepareCopy(po, pos);
                            PickupsCopied++;
                            break;
                        }
                    case EvPickupGone when !NetSession.IsHost:
                        {
                            uint id = r.U32();
                            if (copies.TryGetValue(id, out var p) && p) { copyIdByInstance.Remove(p.GetInstanceID()); Destroy(p.gameObject); }
                            copies.Remove(id);
                            break;
                        }
                    case StatSync.EvStat:
                        StatSync.Apply(ref r);
                        break;
                    case EvGot when !NetSession.IsHost:
                        {
                            int slot = r.U8();
                            int itemId = r.I32();
                            var player = PlayerSlots.ForSlot(slot);
                            var prefab = PickupObjectDatabase.GetById(itemId);
                            if (player && prefab) { LootEngine.GivePrefabToPlayer(prefab.gameObject, player); GivesApplied++; }
                            break;
                        }
                    case EvChest when !NetSession.IsHost:
                        {
                            uint id = r.U32();
                            string name = r.Str();
                            var pos = new Vector2(r.F32(), r.F32());
                            bool open = r.Bool();
                            // Re-announcements (resync, late join) update the chest we already have.
                            if (clientChests.TryGetValue(id, out var known) && known)
                            {
                                if (open && !known.IsOpen) known.ForceOpen(PlayerSlots.Local);
                                break;
                            }
                            var ch = NearestChest(pos) ?? SpawnChest(name, pos);
                            if (ch == null) break;
                            clientChests[id] = ch;
                            chestIdByInstance[ch.GetInstanceID()] = id;
                            ChestsMapped++;
                            if (open && !ch.IsOpen) ch.ForceOpen(PlayerSlots.Local);
                            break;
                        }
                    case EvChestOpened when !NetSession.IsHost:
                        {
                            uint id = r.U32();
                            if (clientChests.TryGetValue(id, out var ch) && ch && !ch.IsOpen) { ch.ForceOpen(PlayerSlots.Local); ChestsOpened++; }
                            break;
                        }
                    case EvChestGone when !NetSession.IsHost:
                        {
                            uint id = r.U32();
                            if (clientChests.TryGetValue(id, out var ch) && ch) { chestIdByInstance.Remove(ch.GetInstanceID()); Destroy(ch.gameObject); }
                            clientChests.Remove(id);
                            break;
                        }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("PickupSync " + type + ": " + e); }
            finally { scope = false; }
        }

        static void PrepareCopy(PickupObject po, Vector2 pos)
        {
            // A dropped item normally arrives as debris that settles; the copy is placed where the host's is.
            var debris = po.GetComponent<DebrisObject>();
            if (debris) debris.enabled = false;
            if (po.specRigidbody) po.specRigidbody.Reinitialize();
            var room = pos.GetAbsoluteRoom();
            if (po is IPlayerInteractable ia && room != null) room.RegisterInteractable(ia);
        }

        static Chest NearestChest(Vector2 pos)
        {
            Chest best = null;
            float bestD = 1f;
            foreach (var ch in StaticReferenceManager.AllChests)
            {
                if (!ch || chestIdByInstance.ContainsKey(ch.GetInstanceID())) continue;
                float d = ((Vector2)ch.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = ch; }
            }
            return best;
        }

        static Chest SpawnChest(string name, Vector2 pos)
        {
            var rm = GameManager.Instance.RewardManager;
            foreach (var c in new[] { rm.D_Chest, rm.C_Chest, rm.B_Chest, rm.A_Chest, rm.S_Chest, rm.Rainbow_Chest, rm.Synergy_Chest })
                if (c != null && c.name == name)
                    return Chest.Spawn(c, new Vector3(pos.x, pos.y, 0f), pos.GetAbsoluteRoom(), true);
            Plugin.Log.LogWarning("PickupSync: unknown chest " + name);
            return null;
        }

        // ---------------- Frame ----------------

        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try { LateUpdateImpl(); }
            finally { Dev.NetProfiler.End("pickups", t0); }
        }

        void LateUpdateImpl()
        {
            if (!NetSession.Active || !SceneObjects.OnDungeonFloor) return;
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 0.1f;
            if (NetSession.IsHost) { HostScan(); return; }
            if (WorldSync.Instance == null || !WorldSync.Instance.FloorReady) return;
            // Client: anything on the ground that the host did not send was generated here; remove it.
            foreach (var p in new List<PickupObject>(WorldPickups()))
            {
                if (!InWorld(p) || copyIdByInstance.ContainsKey(p.GetInstanceID())) continue;
                Destroy(p.gameObject);
                LocalPurged++;
            }
        }
    }
}
