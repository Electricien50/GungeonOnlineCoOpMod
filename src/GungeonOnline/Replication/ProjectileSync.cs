using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Enemy and trap bullets (anything not fired by a player that can hit players).
    /// Host: announces each one (prefab name, position, velocity), sends a correction whenever the real bullet
    /// drifts from the straight line the client is flying it on (curving and scripted patterns), and a destroy.
    /// Client: flies copies at the host's velocity and destroys any such bullet it made itself (replicas play the
    /// host's animations, which would otherwise fire local bullets). Hits on your own character are decided on
    /// your machine; the host's bullets pass through the host's copy of you.
    /// </summary>
    [DefaultExecutionOrder(31600)]
    internal sealed class ProjectileSync : MonoBehaviour
    {
        public static ProjectileSync Instance { get; private set; }
        const byte EvProjSpawn = 20, EvProjUpdate = 21, EvProjDestroy = 22;
        const float DriftUnits = 0.15f;

        /// <summary>Instance ids of bullet copies made from host events (their Start runs a frame later).</summary>
        static readonly HashSet<int> copyIds = new HashSet<int>();
        static bool IsClient => NetSession.Active && !NetSession.IsHost;
        static bool IsHostSession => NetSession.Active && NetSession.IsHost;

        sealed class Tracked { public Projectile P; public Vector2 Pos, Vel; public float At; }
        readonly Dictionary<uint, Tracked> hostTracked = new Dictionary<uint, Tracked>();
        readonly Dictionary<uint, Projectile> copies = new Dictionary<uint, Projectile>();
        static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        uint nextId = 1;
        public int Announced, Updates, Copies, LocalSuppressed, UnknownPrefabs;
        readonly WireWriter w = new WireWriter(128);

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Start)), prefix: new HarmonyMethod(typeof(ProjectileSync), nameof(StartPrefix)), postfix: new HarmonyMethod(typeof(ProjectileSync), nameof(StartPostfix)));
        }

        void Awake() { Instance = this; }
        void Start() { NetSession.Instance.SessionEnded += () => { hostTracked.Clear(); copies.Clear(); copyIds.Clear(); }; }

        /// <summary>Bullets the host owns: not from a player, able to hit players.</summary>
        static bool HostOwned(Projectile p) => p != null && p.collidesWithPlayer && !(p.Owner is PlayerController);

        static string Key(Projectile p)
        {
            var n = p.gameObject.name;
            int i = n.IndexOf("(Clone)");
            return (i >= 0 ? n.Substring(0, i) : n).Trim();
        }

        /// <summary>Remember bullet prefabs from every enemy we spawn so the client can build copies by name.</summary>
        public static void LearnBank(AIActor prefab)
        {
            var bank = prefab != null ? prefab.bulletBank : null;
            if (bank == null || bank.Bullets == null) return;
            foreach (var b in bank.Bullets)
                if (b != null && b.BulletObject != null && !prefabs.ContainsKey(b.BulletObject.name))
                    prefabs[b.BulletObject.name] = b.BulletObject;
        }

        static bool StartPrefix(Projectile __instance)
        {
            if (!IsClient || copyIds.Contains(__instance.GetInstanceID()) || !HostOwned(__instance)) return true;
            // Locally made enemy bullet on the client: the host's copy is the real one.
            Instance.LocalSuppressed++;
            Object.Destroy(__instance.gameObject);
            return false;
        }

        static void StartPostfix(Projectile __instance)
        {
            if (!NetSession.Active || !HostOwned(__instance)) return;
            IgnoreRemotePlayers(__instance);
            if (IsHostSession) Instance.Announce(__instance);
        }

        /// <summary>Each machine judges hits only on its own player.</summary>
        static void IgnoreRemotePlayers(Projectile p)
        {
            if (p.specRigidbody == null) return;
            foreach (var kv in PlayerSlots.Remotes)
                if (kv.Value && kv.Value.specRigidbody) p.specRigidbody.RegisterSpecificCollisionException(kv.Value.specRigidbody);
        }

        void Announce(Projectile p)
        {
            uint id = nextId++;
            Vector2 pos = p.transform.position;
            Vector2 vel = p.Direction * p.Speed;
            hostTracked[id] = new Tracked { P = p, Pos = pos, Vel = vel, At = Time.time };
            p.OnDestruction += proj => OnHostDestroyed(id, proj);
            w.Reset();
            w.U8(EvProjSpawn); w.U32(id); w.Str(Key(p));
            w.F32(pos.x); w.F32(pos.y); w.F32(vel.x); w.F32(vel.y);
            w.U32(NetIds.Of(p.Owner as AIActor));
            Send(w.ToArray());
            Announced++;
        }

        void OnHostDestroyed(uint id, Projectile p)
        {
            if (!hostTracked.Remove(id)) return;
            Vector2 pos = p ? (Vector2)p.transform.position : Vector2.zero;
            w.Reset(); w.U8(EvProjDestroy); w.U32(id); w.F32(pos.x); w.F32(pos.y);
            Send(w.ToArray());
        }

        void Send(byte[] data)
        {
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, data, Delivery.ReliableOrdered);
        }

        /// <summary>Host: send a correction for bullets whose path left the straight line the client predicts.</summary>
        void LateUpdate()
        {
            if (!IsHostSession || hostTracked.Count == 0) return;
            float now = Time.time;
            var dead = new List<uint>();
            foreach (var kv in hostTracked)
            {
                var t = kv.Value;
                if (!t.P) { dead.Add(kv.Key); continue; }
                Vector2 predicted = t.Pos + t.Vel * (now - t.At);
                Vector2 actual = t.P.transform.position;
                if ((actual - predicted).sqrMagnitude < DriftUnits * DriftUnits) continue;
                t.Pos = actual; t.Vel = t.P.specRigidbody != null ? t.P.specRigidbody.Velocity : t.P.Direction * t.P.Speed; t.At = now;
                w.Reset(); w.U8(EvProjUpdate); w.U32(kv.Key);
                w.F32(actual.x); w.F32(actual.y); w.F32(t.Vel.x); w.F32(t.Vel.y);
                Send(w.ToArray());
                Updates++;
            }
            foreach (var id in dead) hostTracked.Remove(id);
        }

        /// <summary>Client side of the event stream (called by WorldSync for types 20 to 29).</summary>
        public void Apply(byte type, ref WireReader r)
        {
            switch (type)
            {
                case EvProjSpawn:
                    {
                        uint id = r.U32();
                        string key = r.Str();
                        var pos = new Vector2(r.F32(), r.F32());
                        var vel = new Vector2(r.F32(), r.F32());
                        var owner = NetIds.Get<AIActor>(r.U32());
                        if (!prefabs.TryGetValue(key, out var prefab))
                        {
                            UnknownPrefabs++;
                            foreach (var any in prefabs.Values) { prefab = any; break; } // still dangerous, maybe wrong look
                            if (prefab == null) return;
                        }
                        var go = SpawnManager.SpawnProjectile(prefab, pos, Quaternion.Euler(0, 0, BraveMathCollege.Atan2Degrees(vel)));
                        var p = go != null ? go.GetComponent<Projectile>() : null;
                        if (p == null) return;
                        copyIds.Add(p.GetInstanceID());
                        MakeCopy(p, owner, vel);
                        copies[id] = p;
                        Copies++;
                        break;
                    }
                case EvProjUpdate:
                    {
                        uint id = r.U32();
                        var pos = new Vector2(r.F32(), r.F32());
                        var vel = new Vector2(r.F32(), r.F32());
                        if (!copies.TryGetValue(id, out var p) || !p) { copies.Remove(id); return; }
                        p.transform.position = new Vector3(pos.x, pos.y, p.transform.position.z);
                        if (p.specRigidbody) p.specRigidbody.Reinitialize();
                        SetVelocity(p, vel);
                        break;
                    }
                case EvProjDestroy:
                    {
                        uint id = r.U32();
                        var pos = new Vector2(r.F32(), r.F32());
                        if (copies.TryGetValue(id, out var p) && p)
                        {
                            copyIds.Remove(p.GetInstanceID());
                            p.transform.position = new Vector3(pos.x, pos.y, p.transform.position.z);
                            p.DieInAir();
                        }
                        copies.Remove(id);
                        break;
                    }
            }
        }

        static void MakeCopy(Projectile p, AIActor owner, Vector2 vel)
        {
            if (owner) p.Owner = owner;
            p.collidesWithEnemies = false;
            // The host flies the real pattern; the copy only follows its velocity.
            var script = p.GetComponent<BulletScriptBehavior>();
            if (script) Object.Destroy(script);
            p.IsBulletScript = false;
            p.baseData.UsesCustomAccelerationCurve = false;
            p.baseData.damping = 0f;
            p.angularVelocity = 0f;
            p.baseData.speed = vel.magnitude;
            p.baseData.range = 1000f;
            SetVelocity(p, vel);
            IgnoreRemotePlayers(p);
        }

        static void SetVelocity(Projectile p, Vector2 vel)
        {
            float s = vel.magnitude;
            if (s < 1e-4f) { p.Speed = 0f; return; }
            p.baseData.speed = s;
            p.Speed = s;
            p.SendInDirection(vel / s, false);
        }
    }
}
