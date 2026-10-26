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
    /// Host: announces each one (prefab, position, velocity) and a destroy, reliably. Where each bullet is now goes
    /// unreliably, batched once a frame on its own channel, so a lost packet never holds bullets back: whenever the
    /// real bullet leaves the path the client is flying it on, and every so often for bullets that curve. A path is
    /// a velocity plus a turn rate and a speed change, so curving patterns need few corrections.
    /// Client: flies copies along that path and destroys any such bullet it made itself (replicas play the host's
    /// animations, which would otherwise fire local bullets). Hits on your own character are decided on your
    /// machine; the host's bullets pass through the host's copy of you. A copy that just appeared or just jumped to
    /// a correction cannot hit you for a moment: you could not have seen it coming.
    /// </summary>
    [DefaultExecutionOrder(31600)]
    internal sealed class ProjectileSync : MonoBehaviour
    {
        public static ProjectileSync Instance { get; private set; }
        // 20-22: the old protocol (an older host); 23-24: bullet names sent once, then a short spawn.
        const byte EvProjSpawn = 20, EvProjUpdate = 21, EvProjDestroy = 22, EvProjName = 23, EvProjSpawnShort = 24;
        const float DriftUnits = 0.15f;
        const float RefreshSeconds = 0.2f;   // curving bullets: where they are, this often, in case a correction was lost
        const float GraceSeconds = 0.1f;     // a copy that just appeared or jumped cannot hit you yet
        const float JumpForGrace = 0.3f;     // a correction this far is a jump you could not see coming
        const int EntriesPerMessage = 40;    // 20 bytes each: well inside one datagram
        const byte FlagStraight = 1;         // flies straight and stops at walls on the host: the copy can too

        /// <summary>Instance ids of bullet copies made from host events (their Start runs a frame later).</summary>
        static readonly HashSet<int> copyIds = new HashSet<int>();
        /// <summary>Copies (rigidbody ids) that touched our own player: their early end is a real hit, not a loss.</summary>
        static readonly HashSet<int> touchedMe = new HashSet<int>();
        /// <summary>Copies (rigidbody ids) that cannot hit our player until this time.</summary>
        static readonly Dictionary<int, float> graceUntil = new Dictionary<int, float>();
        /// <summary>Copies (projectile ids) of straight bullets: walls stop them here too.</summary>
        static readonly HashSet<int> wallCopies = new HashSet<int>();
        static bool IsClient => NetSession.Active && !NetSession.IsHost;
        static bool IsHostSession => NetSession.Active && NetSession.IsHost;

        sealed class Tracked
        {
            public Projectile P;
            public Vector2 Pos, Vel;            // where the client's copy is, as far as we know
            public float Turn, Accel;           // degrees per second, units per second per second
            public Vector2 PrevVel; public bool PrevValid;
            public float LastSent;
            public bool Straight, Corrected;
        }

        sealed class Copy
        {
            public Projectile P;
            public float Turn, Accel;
            public uint Seq;
            public bool Straight, WallsOn;
        }

        readonly Dictionary<uint, Tracked> hostTracked = new Dictionary<uint, Tracked>();
        readonly Dictionary<uint, Copy> copies = new Dictionary<uint, Copy>();
        static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        // Bullet names: the host numbers them, and tells each guest a name once before it is first used.
        readonly Dictionary<string, ushort> nameIds = new Dictionary<string, ushort>();
        readonly Dictionary<Connection, HashSet<ushort>> namesSent = new Dictionary<Connection, HashSet<ushort>>();
        readonly Dictionary<ushort, string> names = new Dictionary<ushort, string>();
        uint nextId = 1, updateSeq;
        public int Announced, Sent, Updates, Copies, LocalSuppressed, UnknownPrefabs, Resent, EarlyDeaths, EarlyNearMe, WallStops, GraceSaves, StaleUpdates;
        int earlyLogged;
        readonly WireWriter w = new WireWriter(128);
        readonly WireWriter uw = new WireWriter(1024);
        readonly List<uint> dead = new List<uint>();
        readonly List<KeyValuePair<uint, Tracked>> outgoing = new List<KeyValuePair<uint, Tracked>>();

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Start)), prefix: new HarmonyMethod(typeof(ProjectileSync), nameof(StartPrefix)), postfix: new HarmonyMethod(typeof(ProjectileSync), nameof(StartPostfix)));
            h.Patch(AccessTools.Method(typeof(StaticReferenceManager), nameof(StaticReferenceManager.DestroyAllProjectiles)), prefix: new HarmonyMethod(typeof(ProjectileSync), nameof(DestroyAllPrefix)));
        }

        void Awake() { Instance = this; }

        /// <summary>New floor: forget every bullet (copies that died locally never get a destroy event).</summary>
        public void OnFloorChanged() { hostTracked.Clear(); ClearCopies(); }

        void ClearCopies() { copies.Clear(); copyIds.Clear(); touchedMe.Clear(); graceUntil.Clear(); wallCopies.Clear(); }

        void Start()
        {
            NetSession.Instance.SessionEnded += () => { hostTracked.Clear(); ClearCopies(); nameIds.Clear(); namesSent.Clear(); names.Clear(); };
            NetSession.Instance.On(Channel.Bullets, OnBullets);
        }

        /// <summary>Bullets the host owns: not from a player, able to hit players.</summary>
        static bool HostOwned(Projectile p) => p != null && p.collidesWithPlayer && !(p.Owner is PlayerController) &&
            !p.name.StartsWith("ThrownGunProjectile", System.StringComparison.Ordinal);

        /// <summary>
        /// Host: a bullet that keeps its line until it hits a wall (no script, curve, homing or bounce). The client can
        /// fly its copy exactly, walls included.
        /// </summary>
        static bool Straight(Projectile p) =>
            p.GetType() == typeof(Projectile) && !p.IsBulletScript && !p.GetComponent<BulletScriptBehavior>() &&
            !p.baseData.UsesCustomAccelerationCurve && Mathf.Abs(p.baseData.damping) < 1e-4f && Mathf.Abs(p.angularVelocity) < 1e-3f &&
            p.specRigidbody != null && p.specRigidbody.CollideWithTileMap &&
            !p.GetComponent<BounceProjModifier>() && !p.GetComponent<HomingModifier>();

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

        static readonly HashSet<string> missing = new HashSet<string>();

        /// <summary>A bullet no enemy bank named (gun bullets, bullets from enemies the game placed itself):
        /// look for the loaded prefab by name once, and remember it.</summary>
        static bool FindLoaded(string key, out GameObject prefab)
        {
            prefab = null;
            if (missing.Contains(key)) return false;
            foreach (var p in Resources.FindObjectsOfTypeAll<Projectile>())
                if (p != null && p.gameObject.name == key && !p.gameObject.scene.IsValid()) { prefab = p.gameObject; break; }
            if (prefab != null) { prefabs[key] = prefab; return true; }
            missing.Add(key);
            Plugin.Log.LogInfo("ProjectileSync: no prefab for bullet " + key);
            return false;
        }

        static bool StartPrefix(Projectile __instance)
        {
            if (!IsClient || copyIds.Contains(__instance.GetInstanceID()) || !HostOwned(__instance)) return true;
            // Locally made enemy bullet on the client: the host's copy is the real one.
            Instance.LocalSuppressed++;
            Object.Destroy(__instance.gameObject);
            return false;
        }

        /// <summary>Guest: a boss intro (or anything else) that clears every bullet on screen leaves the host's bullet
        /// copies alone. On the host those bullets keep flying, and the host says when each one ends.</summary>
        static bool DestroyAllPrefix()
        {
            if (!NetSession.Active || !IsClient || copyIds.Count == 0) return true;
            var list = new List<Projectile>();
            foreach (var p in StaticReferenceManager.AllProjectiles)
                if (p && !copyIds.Contains(p.GetInstanceID())) list.Add(p);
            foreach (var p in list) p.DieInAir(false, false);
            return false;
        }

        static void StartPostfix(Projectile __instance)
        {
            if (!NetSession.Active || !HostOwned(__instance)) return;
            IgnoreRemotePlayers(__instance);
            if (IsClient && copyIds.Contains(__instance.GetInstanceID())) OnlyHitsMe(__instance); // Start may reset collision settings
            if (IsHostSession) Instance.Announce(__instance);
        }

        /// <summary>
        /// Guest: a copy of a host bullet only ever touches our own player (that hit is ours to judge). Enemies and
        /// everything else are the host's call: its bullet may spawn inside the boss that fired it. Walls stop only the
        /// copy of a straight bullet, once it is out of the wall it may have been fired from (wall turrets); a curving
        /// copy could clip a wall its bullet curves past, so it waits for the host's destroy at the real impact point.
        /// </summary>
        static void OnlyHitsMe(Projectile p)
        {
            var rb = p.specRigidbody;
            if (rb == null) return;
            var c = Instance != null ? Instance.CopyOf(p) : null;
            rb.CollideWithTileMap = c != null && c.WallsOn;
            rb.OnPreRigidbodyCollision -= CopyPreCollision;
            rb.OnPreRigidbodyCollision += CopyPreCollision;
        }

        Copy CopyOf(Projectile p)
        {
            foreach (var c in copies.Values) if (c.P == p) return c;
            return null;
        }

        static void CopyPreCollision(SpeculativeRigidbody me, PixelCollider myCollider, SpeculativeRigidbody other, PixelCollider otherCollider)
        {
            var local = PlayerSlots.Local;
            if (!local || other != local.specRigidbody) { PhysicsEngine.SkipCollision = true; return; }
            if (graceUntil.TryGetValue(me.GetInstanceID(), out var until))
            {
                if (Time.realtimeSinceStartup < until)
                {
                    PhysicsEngine.SkipCollision = true;
                    if (Instance != null) Instance.GraceSaves++;
                    return;
                }
                graceUntil.Remove(me.GetInstanceID());
            }
            touchedMe.Add(me.GetInstanceID());
        }

        static void Grace(Projectile p)
        {
            if (p && p.specRigidbody) graceUntil[p.specRigidbody.GetInstanceID()] = Time.realtimeSinceStartup + GraceSeconds;
        }

        /// <summary>Each machine judges hits only on its own player.</summary>
        static void IgnoreRemotePlayers(Projectile p)
        {
            if (p.specRigidbody == null) return;
            foreach (var kv in PlayerSlots.Remotes)
                if (kv.Value && kv.Value.specRigidbody) p.specRigidbody.RegisterSpecificCollisionException(kv.Value.specRigidbody);
        }

        static Vector2 VelocityOf(Projectile p) => p.specRigidbody != null ? p.specRigidbody.Velocity : p.Direction * p.Speed;

        // ---------------- Host ----------------

        void Announce(Projectile p)
        {
            uint id = nextId++;
            Vector2 pos = p.transform.position;
            Vector2 vel = p.Direction * p.Speed;
            bool straight = Straight(p);
            hostTracked[id] = new Tracked { P = p, Pos = pos, Vel = vel, LastSent = Time.realtimeSinceStartup, Straight = straight };
            p.OnDestruction += proj => OnHostDestroyed(id, proj);
            if (SendSpawn(id, p, pos, vel, straight, null) > 0) Sent++;
            Announced++;
        }

        ushort NameId(string key)
        {
            if (!nameIds.TryGetValue(key, out var n)) nameIds[key] = n = (ushort)nameIds.Count;
            return n;
        }

        /// <summary>The spawn of one bullet to every ready guest, or only to <paramref name="only"/>; a guest that has not
        /// had this bullet's name yet gets it first (same reliable stream, so it arrives first).</summary>
        int SendSpawn(uint id, Projectile p, Vector2 pos, Vector2 vel, bool straight, Connection only)
        {
            ushort name = NameId(Key(p));
            w.Reset();
            w.U8(EvProjSpawnShort); w.U32(id); w.U16(name);
            w.F32(pos.x); w.F32(pos.y); w.F32(vel.x); w.F32(vel.y);
            w.U32(NetIds.Of(p.Owner as AIActor));
            w.U8(straight ? FlagStraight : (byte)0);
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            int n = 0;
            foreach (var c in NetSession.Instance.Node.Connections)
            {
                if (only != null ? c != only : (ws == null || !ws.IsReady(c))) continue;
                if (!namesSent.TryGetValue(c, out var known)) namesSent[c] = known = new HashSet<ushort>();
                if (known.Add(name))
                {
                    var nw = new WireWriter(64);
                    nw.U8(EvProjName); nw.U16(name); nw.Str(Key(p));
                    c.Send((byte)Channel.Event, nw.ToArray(), Delivery.ReliableOrdered);
                }
                c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
                n++;
            }
            return n;
        }

        /// <summary>Host: a guest just finished loading the floor; send every enemy bullet still in the air
        /// (bullets fired while it was loading were never sent to it).</summary>
        public void SendAllTo(Connection c)
        {
            namesSent.Remove(c); // a guest that (re)loaded may have restarted: tell it the names again
            foreach (var kv in hostTracked)
            {
                var p = kv.Value.P;
                if (!p) continue;
                SendSpawn(kv.Key, p, p.transform.position, VelocityOf(p), kv.Value.Straight, c);
                Resent++;
            }
        }

        void OnHostDestroyed(uint id, Projectile p)
        {
            if (!hostTracked.Remove(id)) return;
            Vector2 pos = p ? (Vector2)p.transform.position : Vector2.zero;
            w.Reset(); w.U8(EvProjDestroy); w.U32(id); w.F32(pos.x); w.F32(pos.y);
            var data = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, data, Delivery.ReliableOrdered);
        }

        static Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad, cs = Mathf.Cos(r), sn = Mathf.Sin(r);
            return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        /// <summary>One frame along a path: turn, speed up or slow down, move. Host and client step it the same way.</summary>
        static Vector2 Step(Vector2 vel, float turn, float accel, float dt)
        {
            if (turn != 0f) vel = Rotate(vel, turn * dt);
            if (accel != 0f)
            {
                float s = vel.magnitude;
                if (s > 1e-4f) vel *= Mathf.Max(0f, s + accel * dt) / s;
            }
            return vel;
        }

        /// <summary>Host: follow the path each copy is flying, and send where the bullet really is when it left that
        /// path, or (curving bullets) every so often in case a correction was lost on the way.</summary>
        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try { LateUpdateImpl(); }
            finally { Dev.NetProfiler.End("bullets", t0); }
        }

        void LateUpdateImpl()
        {
            if (IsClient) { ClientUpdate(); return; }
            if (!IsHostSession || hostTracked.Count == 0) return;
            float now = Time.realtimeSinceStartup, dt = BraveTime.DeltaTime;
            dead.Clear();
            outgoing.Clear();
            foreach (var kv in hostTracked)
            {
                var t = kv.Value;
                if (!t.P) { dead.Add(kv.Key); continue; }
                Vector2 actual = t.P.transform.position;
                Vector2 vel = VelocityOf(t.P);
                // How the bullet is turning and changing speed right now (scripted patterns change smoothly).
                float turn = 0f, accel = 0f;
                if (t.PrevValid && dt > 1e-4f && t.PrevVel.sqrMagnitude > 1e-6f && vel.sqrMagnitude > 1e-6f)
                {
                    turn = Mathf.Clamp(Vector2.Angle(t.PrevVel, vel) * Mathf.Sign(t.PrevVel.x * vel.y - t.PrevVel.y * vel.x) / dt, -720f, 720f);
                    accel = Mathf.Clamp((vel.magnitude - t.PrevVel.magnitude) / dt, -120f, 120f);
                    if (Mathf.Abs(turn) < 2f) turn = 0f;
                    if (Mathf.Abs(accel) < 0.5f) accel = 0f;
                }
                t.PrevVel = vel; t.PrevValid = true;
                // Where the copy is now: its path, one frame on (it moves, then turns, as the client steps it).
                t.Pos += t.Vel * dt;
                t.Vel = Step(t.Vel, t.Turn, t.Accel, dt);
                bool off = (actual - t.Pos).sqrMagnitude >= DriftUnits * DriftUnits;
                bool due = (!t.Straight || t.Corrected) && now - t.LastSent >= RefreshSeconds;
                if (!off && !due) continue;
                t.Pos = actual; t.Vel = vel; t.Turn = turn; t.Accel = accel; t.LastSent = now; t.Corrected = true;
                outgoing.Add(kv);
                Updates++;
            }
            foreach (var id in dead) hostTracked.Remove(id);
            if (outgoing.Count > 0) SendUpdates();
        }

        /// <summary>Batched, unreliable: a lost one is followed by a fresher one, and nothing waits for it.</summary>
        void SendUpdates()
        {
            var ws = WorldSync.Instance;
            for (int start = 0; start < outgoing.Count; start += EntriesPerMessage)
            {
                int n = Mathf.Min(EntriesPerMessage, outgoing.Count - start);
                uw.Reset();
                uw.U32(++updateSeq);
                uw.U8((byte)n);
                for (int i = start; i < start + n; i++)
                {
                    var t = outgoing[i].Value;
                    uw.U32(outgoing[i].Key);
                    uw.F32(t.Pos.x); uw.F32(t.Pos.y);
                    uw.I16(Q(t.Vel.x, 256f)); uw.I16(Q(t.Vel.y, 256f));
                    uw.I16(Q(t.Turn, 16f)); uw.I16(Q(t.Accel, 128f));
                }
                var data = uw.ToArray();
                foreach (var c in NetSession.Instance.Node.Connections)
                    if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Bullets, data, Delivery.Unreliable);
            }
        }

        static short Q(float v, float scale) => (short)Mathf.Clamp(Mathf.RoundToInt(v * scale), short.MinValue, short.MaxValue);

        // ---------------- Client ----------------

        /// <summary>Client: copies follow their path (turn and speed change), and straight copies meet walls once
        /// they are out of the wall they may have been fired from.</summary>
        void ClientUpdate()
        {
            if (copies.Count == 0) return;
            float dt = BraveTime.DeltaTime;
            dead.Clear();
            foreach (var kv in copies)
            {
                var c = kv.Value;
                var p = c.P;
                if (!p) { dead.Add(kv.Key); continue; }
                if ((c.Turn != 0f || c.Accel != 0f) && dt > 0f) SetVelocity(p, Step(p.Direction * p.Speed, c.Turn, c.Accel, dt));
                if (c.Straight && !c.WallsOn && p.specRigidbody && OnFloor(p.specRigidbody.UnitCenter))
                {
                    c.WallsOn = true;
                    p.specRigidbody.CollideWithTileMap = true;
                }
            }
            foreach (var id in dead) copies.Remove(id);
        }

        static bool OnFloor(Vector2 at)
        {
            var d = GameManager.Instance.Dungeon != null ? GameManager.Instance.Dungeon.data : null;
            if (d == null) return false;
            var cell = at.ToIntVector2(VectorConversions.Floor);
            if (!d.CheckInBounds(cell.x, cell.y)) return false;
            var cd = d.cellData[cell.x][cell.y];
            return cd != null && cd.type == Dungeonator.CellType.FLOOR;
        }

        void OnBullets(Connection from, byte[] data, Delivery d)
        {
            if (!IsClient) return;
            var r = new WireReader(data, 0, data.Length);
            uint seq = r.U32();
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                uint id = r.U32();
                var pos = new Vector2(r.F32(), r.F32());
                var vel = new Vector2(r.I16() / 256f, r.I16() / 256f);
                float turn = r.I16() / 16f, accel = r.I16() / 128f;
                if (!copies.TryGetValue(id, out var c) || !c.P) continue;
                if (seq <= c.Seq) { StaleUpdates++; continue; } // an older one that arrived late
                c.Seq = seq;
                c.Turn = turn; c.Accel = accel;
                MoveCopy(c.P, pos, vel);
            }
        }

        static void MoveCopy(Projectile p, Vector2 pos, Vector2 vel)
        {
            Vector2 was = p.transform.position;
            if ((pos - was).sqrMagnitude >= JumpForGrace * JumpForGrace) Grace(p);
            DrawSmoothing.Corrected(p, was, pos);
            p.transform.position = new Vector3(pos.x, pos.y, p.transform.position.z);
            if (p.specRigidbody) p.specRigidbody.Reinitialize();
            SetVelocity(p, vel);
        }

        /// <summary>Client side of the event stream (called by WorldSync for types 20 to 29).</summary>
        public void Apply(byte type, ref WireReader r)
        {
            switch (type)
            {
                case EvProjName:
                    {
                        ushort n = r.U16();
                        names[n] = r.Str();
                        break;
                    }
                case EvProjSpawn:
                case EvProjSpawnShort:
                    {
                        uint id = r.U32();
                        string key;
                        if (type == EvProjSpawn) key = r.Str();
                        else names.TryGetValue(r.U16(), out key);
                        var pos = new Vector2(r.F32(), r.F32());
                        var vel = new Vector2(r.F32(), r.F32());
                        var owner = NetIds.Get<AIActor>(r.U32());
                        byte flags = r.Remaining > 0 ? r.U8() : (byte)0;
                        if (copies.TryGetValue(id, out var have) && have.P) break; // already flying (sent again on a refresh)
                        GameObject prefab = null;
                        if (key == null || (!prefabs.TryGetValue(key, out prefab) && !FindLoaded(key, out prefab)))
                        {
                            UnknownPrefabs++;
                            foreach (var any in prefabs.Values) { prefab = any; break; } // still dangerous, maybe wrong look
                            if (prefab == null) return;
                        }
                        var go = SpawnManager.SpawnProjectile(prefab, pos, Quaternion.Euler(0, 0, BraveMathCollege.Atan2Degrees(vel)));
                        var p = go != null ? go.GetComponent<Projectile>() : null;
                        if (p == null) return;
                        copyIds.Add(p.GetInstanceID());
                        var c = new Copy { P = p, Straight = (flags & FlagStraight) != 0 };
                        copies[id] = c;
                        MakeCopy(p, owner, vel);
                        Grace(p);
                        Copies++;
                        // A copy that dies here before the host says so was cut short on this screen: count it.
                        uint cid = id;
                        float born = Time.time;
                        p.OnDestruction += proj =>
                        {
                            if (!copies.TryGetValue(cid, out var cc) || cc.P != proj) return;
                            copies.Remove(cid);
                            copyIds.Remove(proj.GetInstanceID());
                            if (proj.specRigidbody) graceUntil.Remove(proj.specRigidbody.GetInstanceID());
                            if (cc.WallsOn && !(proj.specRigidbody && touchedMe.Contains(proj.specRigidbody.GetInstanceID()))) { WallStops++; return; }
                            EarlyDeaths++;
                            NoteEarly(proj, Time.time - born);
                        };
                        break;
                    }
                case EvProjUpdate:
                    {
                        uint id = r.U32();
                        var pos = new Vector2(r.F32(), r.F32());
                        var vel = new Vector2(r.F32(), r.F32());
                        if (!copies.TryGetValue(id, out var c) || !c.P) { copies.Remove(id); return; }
                        c.Turn = c.Accel = 0f;
                        MoveCopy(c.P, pos, vel);
                        break;
                    }
                case EvProjDestroy:
                    {
                        uint id = r.U32();
                        var pos = new Vector2(r.F32(), r.F32());
                        if (copies.TryGetValue(id, out var c) && c.P)
                        {
                            var p = c.P;
                            copyIds.Remove(p.GetInstanceID());
                            copies.Remove(id);
                            if (p.specRigidbody) graceUntil.Remove(p.specRigidbody.GetInstanceID());
                            p.transform.position = new Vector3(pos.x, pos.y, p.transform.position.z);
                            p.DieInAir();
                        }
                        copies.Remove(id);
                        break;
                    }
            }
        }

        /// <summary>Why a copy died before the host said so: a hit on our own player is correct, anything else is not.</summary>
        void NoteEarly(Projectile proj, float age)
        {
            var me = PlayerSlots.Local;
            float d = me && proj ? Vector2.Distance(proj.transform.position, me.CenterPosition) : 99f;
            if (proj && proj.specRigidbody && touchedMe.Remove(proj.specRigidbody.GetInstanceID())) { EarlyNearMe++; return; }
            if (earlyLogged++ < 3)
                Plugin.Log.LogInfo($"ProjectileSync: copy died early age={age:F2}s dist={d:F1} at={(Vector2)proj.transform.position} speed={proj.Speed:F1} room={(me && me.CurrentRoom != null ? me.CurrentRoom.GetRoomName() : "-")}");
        }

        static void MakeCopy(Projectile p, AIActor owner, Vector2 vel)
        {
            if (owner) p.Owner = owner;
            p.collidesWithEnemies = false;
            // The host flies the real pattern; the copy follows the path the host sends.
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
            OnlyHitsMe(p);
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
