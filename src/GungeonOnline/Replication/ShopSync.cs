using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Shops. Stock depends on the host's save, so the host announces every shop item (item, price, sold out)
    /// and the client replaces its own stock with it. Buying or stealing on the client is a request to the host;
    /// the wallet, inventory and sold-out state then follow from the vitals, inventory and shop updates.
    /// </summary>
    [DefaultExecutionOrder(31850)]
    internal sealed class ShopSync : MonoBehaviour
    {
        public static ShopSync Instance { get; private set; }
        const byte EvShopItem = 60, EvShopReq = 61;

        static bool scope;
        static bool IsClient => NetSession.Active && !NetSession.IsHost;

        readonly Dictionary<uint, ShopItemController> hostItems = new Dictionary<uint, ShopItemController>();
        readonly Dictionary<uint, string> lastKey = new Dictionary<uint, string>();
        readonly Dictionary<uint, ShopItemController> clientItems = new Dictionary<uint, ShopItemController>();
        static readonly Dictionary<int, uint> clientIdByInstance = new Dictionary<int, uint>();
        uint nextId = 1;
        float nextScan;
        public int Announced, Mapped, Restocked, Requests;
        readonly WireWriter w = new WireWriter(96);

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(ShopItemController), nameof(ShopItemController.Interact)),
                prefix: new HarmonyMethod(typeof(ShopSync), nameof(InteractPrefix)));
        }

        void Awake() { Instance = this; }
        void Start() { NetSession.Instance.SessionEnded += OnFloorChanged; }

        public void OnFloorChanged()
        {
            hostItems.Clear(); lastKey.Clear(); clientItems.Clear(); clientIdByInstance.Clear();
        }

        static bool InteractPrefix(ShopItemController __instance, PlayerController player)
        {
            if (!IsClient || scope) return true;
            if (clientIdByInstance.TryGetValue(__instance.GetInstanceID(), out var id) && player == PlayerSlots.Local)
            {
                var w = Instance.w;
                w.Reset(); w.U8(EvShopReq); w.U32(id);
                NetSession.Instance.Node.Broadcast((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
                NetSession.Instance.FlushNow();
                Instance.Requests++;
            }
            return false;
        }

        static string KeyOf(ShopItemController s) =>
            $"{(s.item ? s.item.PickupObjectId : -1)}/{s.CurrentPrice}/{s.Acquired}/{s.gameObject.activeInHierarchy}";

        byte[] Msg(uint id, ShopItemController s)
        {
            var ww = new WireWriter(48);
            ww.U8(EvShopItem); ww.U32(id);
            ww.I32(s.item ? s.item.PickupObjectId : -1);
            ww.I32(s.CurrentPrice);
            Vector2 pos = s.transform.position;
            ww.F32(pos.x); ww.F32(pos.y);
            ww.Bool(s.Acquired);
            return ww.ToArray();
        }

        void Send(byte[] data)
        {
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, data, Delivery.ReliableOrdered);
        }

        public void SendAllTo(Connection c)
        {
            foreach (var kv in hostItems) if (kv.Value) c.Send((byte)Channel.Event, Msg(kv.Key, kv.Value), Delivery.ReliableOrdered);
        }

        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try { LateUpdateImpl(); }
            finally { Dev.NetProfiler.End("shops", t0); }
        }

        void LateUpdateImpl()
        {
            if (!NetSession.Active || !NetSession.IsHost || !SceneObjects.OnDungeonFloor) return;
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 0.25f;
            foreach (var s in SceneObjects.All<ShopItemController>())
            {
                if (!s) continue;
                uint id = 0;
                foreach (var kv in hostItems) if (kv.Value == s) { id = kv.Key; break; }
                if (id == 0) { id = nextId++; hostItems[id] = s; Announced++; }
                string key = KeyOf(s);
                if (lastKey.TryGetValue(id, out var prev) && prev == key) continue;
                lastKey[id] = key;
                Send(Msg(id, s));
            }
            foreach (var id in new List<uint>(hostItems.Keys))
            {
                if (hostItems[id]) continue;
                hostItems.Remove(id);
                w.Reset(); w.U8(EvShopItem); w.U32(id); w.I32(-1); w.I32(0); w.F32(0); w.F32(0); w.Bool(true);
                Send(w.ToArray());
            }
        }

        public void Apply(Connection c, byte type, ref WireReader r)
        {
            if (type == EvShopReq && NetSession.IsHost)
            {
                uint id = r.U32();
                var p = PlayerSlots.ForSlot(c.Slot);
                if (hostItems.TryGetValue(id, out var s) && s && p) s.Interact(p);
                return;
            }
            if (type != EvShopItem || NetSession.IsHost) return;
            uint sid = r.U32();
            int itemId = r.I32();
            int price = r.I32();
            var pos = new Vector2(r.F32(), r.F32());
            bool gone = r.Bool();
            if (!clientItems.TryGetValue(sid, out var mine) || !mine)
            {
                mine = Nearest(pos);
                if (mine == null) return;
                clientItems[sid] = mine;
                clientIdByInstance[mine.GetInstanceID()] = sid;
                Mapped++;
            }
            scope = true;
            try
            {
                if (gone) { mine.ForceOutOfStock(); return; }
                if (itemId >= 0 && (!mine.item || mine.item.PickupObjectId != itemId))
                {
                    var prefab = PickupObjectDatabase.GetById(itemId);
                    var parent = Traverse.Create(mine).Field("m_baseParentShop").GetValue<BaseShopController>();
                    if (prefab != null && parent != null) { mine.Initialize(prefab, parent); Restocked++; }
                }
                mine.CurrentPrice = price;
            }
            catch (System.Exception e) { Plugin.Log.LogError("ShopSync: " + e.Message); }
            finally { scope = false; }
        }

        static ShopItemController Nearest(Vector2 pos)
        {
            ShopItemController best = null;
            float bestD = 1.5f;
            foreach (var s in SceneObjects.All<ShopItemController>())
            {
                if (!s || clientIdByInstance.ContainsKey(s.GetInstanceID())) continue;
                float d = ((Vector2)s.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; }
            }
            return best;
        }
    }
}
