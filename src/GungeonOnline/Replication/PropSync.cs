using System.Collections.Generic;
using Dungeonator;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Props the floor generator places identically on every machine: pots and crates, barrels, tables and minecarts.
    /// The host decides when they break or flip and where carts are; the client follows. Objects are matched by
    /// position because both machines build the same floor from the shared seed.
    /// </summary>
    [DefaultExecutionOrder(31900)]
    internal sealed class PropSync : MonoBehaviour
    {
        public static PropSync Instance { get; private set; }
        const byte EvMinor = 80, EvMajor = 81, EvFlip = 82, EvCarts = 83;

        static bool scope;
        static bool IsClient => NetSession.Active && !NetSession.IsHost;
        static bool IsHostSession => NetSession.Active && NetSession.IsHost;

        List<MineCartController> carts;
        float nextCarts;
        public int Breaks, Flips, CartUpdates;
        readonly WireWriter w = new WireWriter(512);

        public static void Install(Harmony h)
        {
            var t = typeof(PropSync);
            foreach (var m in typeof(MinorBreakable).GetMethods())
                if (m.Name == nameof(MinorBreakable.Break) && m.DeclaringType == typeof(MinorBreakable))
                    h.Patch(m, prefix: new HarmonyMethod(t, nameof(ClientBlock)), postfix: new HarmonyMethod(t, nameof(MinorPost)));
            h.Patch(AccessTools.Method(typeof(MajorBreakable), nameof(MajorBreakable.Break)), prefix: new HarmonyMethod(t, nameof(ClientBlock)), postfix: new HarmonyMethod(t, nameof(MajorPost)));
            h.Patch(AccessTools.Method(typeof(FlippableCover), nameof(FlippableCover.Flip), new[] { typeof(DungeonData.Direction) }),
                prefix: new HarmonyMethod(t, nameof(ClientBlock)), postfix: new HarmonyMethod(t, nameof(FlipPost)));
        }

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.On(Channel.Props, (c, data, d) =>
            {
                if (data.Length == 0 || data[0] != EvCarts || WorldSync.Instance == null || !WorldSync.Instance.FloorReady) return;
                var r = new WireReader(data, 1, data.Length - 1);
                ApplyCarts(ref r);
            });
        }
        public void OnFloorChanged() { carts = null; }

        static bool ClientBlock() => !IsClient || scope;

        void Send(byte[] data)
        {
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, data, Delivery.ReliableOrdered);
        }

        void SendAt(byte type, Vector2 p, int extra = 0)
        {
            w.Reset(); w.U8(type); w.F32(p.x); w.F32(p.y); w.I32(extra);
            Send(w.ToArray());
        }

        static void MinorPost(MinorBreakable __instance)
        {
            if (IsHostSession && !scope && Instance) { Instance.SendAt(EvMinor, __instance.transform.position); Instance.Breaks++; }
        }

        static void MajorPost(MajorBreakable __instance)
        {
            if (IsHostSession && !scope && Instance) { Instance.SendAt(EvMajor, __instance.transform.position); Instance.Breaks++; }
        }

        static void FlipPost(FlippableCover __instance, DungeonData.Direction flipDirection)
        {
            if (IsHostSession && !scope && Instance) { Instance.SendAt(EvFlip, __instance.transform.position, (int)flipDirection); Instance.Flips++; }
        }

        static T Nearest<T>(IEnumerable<T> items, Vector2 p, System.Func<T, bool> ok) where T : Component
        {
            T best = null;
            float bestD = 0.75f * 0.75f;
            foreach (var it in items)
            {
                if (!it || !ok(it)) continue;
                float d = ((Vector2)it.transform.position - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = it; }
            }
            return best;
        }

        List<MineCartController> Carts()
        {
            if (carts == null)
            {
                carts = SceneObjects.All<MineCartController>();
                // Same floor on both machines: order by starting position so index i is the same cart everywhere.
                carts.Sort((a, b) =>
                {
                    Vector2 pa = a.transform.position, pb = b.transform.position;
                    int c = pa.x.CompareTo(pb.x);
                    return c != 0 ? c : pa.y.CompareTo(pb.y);
                });
            }
            return carts;
        }

        void LateUpdate()
        {
            if (!IsHostSession || !SceneObjects.OnDungeonFloor || Time.realtimeSinceStartup < nextCarts) return;
            nextCarts = Time.realtimeSinceStartup + 1f / 30f;
            var list = Carts();
            if (list.Count == 0) return;
            w.Reset(); w.U8(EvCarts); w.U8((byte)Mathf.Min(255, list.Count));
            for (int i = 0; i < list.Count && i < 255; i++)
            {
                Vector2 p = list[i] ? (Vector2)list[i].transform.position : Vector2.zero;
                w.F32(p.x); w.F32(p.y);
            }
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Props, msg, Delivery.Unreliable);
        }

        public void ApplyCarts(ref WireReader r)
        {
            if (!IsClient || !SceneObjects.OnDungeonFloor) return;
            int n = r.U8();
            var list = Carts();
            for (int i = 0; i < n; i++)
            {
                var p = new Vector2(r.F32(), r.F32());
                if (i >= list.Count || !list[i]) continue;
                var cart = list[i];
                if (((Vector2)cart.transform.position - p).sqrMagnitude < 0.0025f) continue;
                cart.transform.position = new Vector3(p.x, p.y, cart.transform.position.z);
                if (cart.specRigidbody) cart.specRigidbody.Reinitialize();
                CartUpdates++;
            }
        }

        /// <summary>Events 80-89 from the host.</summary>
        public void Apply(byte type, ref WireReader r)
        {
            if (NetSession.IsHost) return;
            var p = new Vector2(r.F32(), r.F32());
            int extra = r.I32();
            scope = true;
            try
            {
                switch (type)
                {
                    case EvMinor:
                        {
                            var b = Nearest(StaticReferenceManager.AllMinorBreakables, p, x => !x.IsBroken);
                            if (b) { b.Break(); Breaks++; }
                            break;
                        }
                    case EvMajor:
                        {
                            var b = Nearest(StaticReferenceManager.AllMajorBreakables, p, x => !x.IsDestroyed);
                            if (b) { b.Break(Vector2.zero); Breaks++; }
                            break;
                        }
                    case EvFlip:
                        {
                            var f = Nearest(SceneObjects.All<FlippableCover>(), p, x => !x.IsFlipped);
                            if (f) { f.Flip((DungeonData.Direction)extra); Flips++; }
                            break;
                        }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError("PropSync: " + e.Message); }
            finally { scope = false; }
        }
    }
}
