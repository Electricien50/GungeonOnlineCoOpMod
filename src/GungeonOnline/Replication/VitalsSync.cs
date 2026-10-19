using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Player health, armor, blanks and the shared wallet.
    /// Hits on a player are decided by the machine that owns that player (what you see dodge is a dodge):
    /// the client reports each hit on its own character; the host applies it to its copy. Everything else that
    /// changes vitals (pickups, shops, healing) happens on the host, which sends every player's vitals when they
    /// change. A client accepts the host's numbers for its own player only once the host has caught up with all
    /// of its hit reports, so health never flickers back up.
    /// </summary>
    [DefaultExecutionOrder(31700)]
    internal sealed class VitalsSync : MonoBehaviour
    {
        public static VitalsSync Instance { get; private set; }
        const byte EvHurt = 40, EvVitals = 41;

        static bool scope;
        static bool IsClient => NetSession.Active && !NetSession.IsHost;
        static bool IsHostSession => NetSession.Active && NetSession.IsHost;

        uint hurtSeq;                                        // client: reports sent
        readonly Dictionary<int, uint> hurtApplied = new Dictionary<int, uint>(); // host: per slot, last report applied
        readonly Dictionary<int, string> lastVitals = new Dictionary<int, string>();
        public int HurtsSent, HurtsApplied, VitalsSent, VitalsApplied;
        readonly WireWriter w = new WireWriter(128);

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(HealthHaver), nameof(HealthHaver.ApplyDamage)),
                prefix: new HarmonyMethod(typeof(VitalsSync), nameof(DamagePrefix)),
                postfix: new HarmonyMethod(typeof(VitalsSync), nameof(DamagePostfix)));
        }

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.SessionEnded += () => { hurtSeq = 0; hurtApplied.Clear(); lastVitals.Clear(); };
        }

        /// <summary>A copy of another machine's player never takes damage from local hit checks.</summary>
        static bool DamagePrefix(HealthHaver __instance)
        {
            if (!NetSession.Active || scope || __instance == null) return true;
            var p = __instance.GetComponent<PlayerController>();
            return p == null || !PlayerSlots.IsRemote(p);
        }

        /// <summary>Client: report every hit on its own player to the host.</summary>
        static void DamagePostfix(HealthHaver __instance, float damage, Vector2 direction)
        {
            if (!IsClient || scope || Instance == null || __instance == null) return;
            var p = __instance.GetComponent<PlayerController>();
            if (p == null || p != PlayerSlots.Local) return;
            Instance.hurtSeq++;
            var w = Instance.w;
            w.Reset(); w.U8(EvHurt); w.U32(Instance.hurtSeq);
            w.F32(damage); w.F32(direction.x); w.F32(direction.y);
            w.F32(__instance.GetCurrentHealth()); w.F32(__instance.Armor);
            NetSession.Instance.Node.Broadcast((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
            NetSession.Instance.FlushNow();
            Instance.HurtsSent++;
        }

        /// <summary>Events 40-49, called by WorldSync's event router on both sides.</summary>
        public void Apply(Connection c, byte type, ref WireReader r)
        {
            if (type == EvHurt && NetSession.IsHost)
            {
                uint seq = r.U32();
                float dmg = r.F32();
                var dir = new Vector2(r.F32(), r.F32());
                float hpAfter = r.F32(), armorAfter = r.F32();
                var p = PlayerSlots.ForSlot(c.Slot);
                hurtApplied[c.Slot] = seq;
                if (!p || p.healthHaver == null) return;
                scope = true;
                try
                {
                    // Apply as a real hit (damage reactions, item triggers), then pin to what the owner saw.
                    p.healthHaver.ApplyDamage(dmg, dir, "remote", CoreDamageTypes.None, DamageCategory.Normal, true);
                    p.healthHaver.ForceSetCurrentHealth(hpAfter);
                    p.healthHaver.Armor = armorAfter;
                }
                finally { scope = false; }
                HurtsApplied++;
                lastVitals.Remove(c.Slot); // force a vitals echo with the new hurt sequence
            }
            else if (type == EvVitals && !NetSession.IsHost)
            {
                int n = r.U8();
                for (int i = 0; i < n; i++)
                {
                    int slot = r.U8();
                    uint ack = r.U32();
                    float hp = r.F32(), max = r.F32(), armor = r.F32();
                    int blanks = r.I16();
                    int money = r.I32(), keys = r.I16();
                    var p = PlayerSlots.ForSlot(slot);
                    if (!p || p.healthHaver == null) continue;
                    bool own = slot == NetSession.LocalSlot;
                    if (own && ack < hurtSeq) continue; // the host has not seen all our hits yet
                    scope = true;
                    try
                    {
                        if (Mathf.Abs(p.healthHaver.GetMaxHealth() - max) > 0.01f) p.healthHaver.SetHealthMaximum(max);
                        if (Mathf.Abs(p.healthHaver.GetCurrentHealth() - hp) > 0.01f) p.healthHaver.ForceSetCurrentHealth(hp);
                        if (Mathf.Abs(p.healthHaver.Armor - armor) > 0.01f) p.healthHaver.Armor = armor;
                        if (p.Blanks != blanks) p.Blanks = blanks;
                        if (p.carriedConsumables != null)
                        {
                            if (p.carriedConsumables.Currency != money) p.carriedConsumables.Currency = money;
                            if (p.carriedConsumables.KeyBullets != keys) p.carriedConsumables.KeyBullets = keys;
                        }
                    }
                    finally { scope = false; }
                    VitalsApplied++;
                }
            }
        }

        /// <summary>Host: send vitals for every player whenever any of them changed.</summary>
        void LateUpdate()
        {
            if (!IsHostSession || GameManager.Instance.IsLoadingLevel) return;
            var list = new List<KeyValuePair<int, PlayerController>>();
            if (PlayerSlots.Local) list.Add(new KeyValuePair<int, PlayerController>(NetSession.LocalSlot, PlayerSlots.Local));
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) list.Add(kv);
            bool changed = false;
            foreach (var kv in list)
            {
                string key = Key(kv.Key, kv.Value);
                if (!lastVitals.TryGetValue(kv.Key, out var prev) || prev != key) { lastVitals[kv.Key] = key; changed = true; }
            }
            if (!changed) return;
            w.Reset(); w.U8(EvVitals); w.U8((byte)list.Count);
            foreach (var kv in list)
            {
                var p = kv.Value;
                w.U8((byte)kv.Key);
                w.U32(hurtApplied.TryGetValue(kv.Key, out var a) ? a : 0);
                w.F32(p.healthHaver.GetCurrentHealth()); w.F32(p.healthHaver.GetMaxHealth()); w.F32(p.healthHaver.Armor);
                w.I16((short)p.Blanks);
                w.I32(p.carriedConsumables != null ? p.carriedConsumables.Currency : 0);
                w.I16((short)(p.carriedConsumables != null ? p.carriedConsumables.KeyBullets : 0));
            }
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
            VitalsSent++;
        }

        static string Key(int slot, PlayerController p)
        {
            var hh = p.healthHaver;
            int money = p.carriedConsumables != null ? p.carriedConsumables.Currency : 0;
            int keys = p.carriedConsumables != null ? p.carriedConsumables.KeyBullets : 0;
            return $"{hh.GetCurrentHealth():F2}/{hh.GetMaxHealth():F2}/{hh.Armor:F2}/{p.Blanks}/{money}/{keys}";
        }
    }
}
