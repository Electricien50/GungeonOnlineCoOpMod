using System.Text;
using Dungeonator;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Floor transitions. The host decides when the next floor loads and with which run seed; the client
    /// loads only on the host's word. After every load both machines log a fingerprint of the generated floor
    /// so any difference in generation is visible (and later reconciled by the host's floor manifest).
    /// </summary>
    internal sealed class RunSync : MonoBehaviour
    {
        const byte MsgLoadLevel = 1, MsgRejoin = 2, MsgLayout = 3;
        /// <summary>Index of the floor currently loaded (both machines).</summary>
        public static int CurrentIndex = -1;
        public static RunSync Instance { get; private set; }

        static bool clientAllowed;
        public string LastFingerprint = "-";
        string lastScene;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.LoadNextLevel)),
                prefix: new HarmonyMethod(typeof(RunSync), nameof(LoadNextLevelPrefix)));
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.DelayedLoadNextLevel)),
                prefix: new HarmonyMethod(typeof(RunSync), nameof(DelayedPrefix)));
        }

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.On(Channel.Control, OnControl);
        }

        static Traverse GM => Traverse.Create(GameManager.Instance);

        /// <summary>Client: floor changes only come from the host.</summary>
        static bool DelayedPrefix()
        {
            if (!NetSession.Active || NetSession.IsHost) return true;
            return clientAllowed;
        }

        static bool LoadNextLevelPrefix()
        {
            if (!NetSession.Active) return true;
            var gm = GameManager.Instance;
            if (NetSession.IsHost)
            {
                // Everyone must have picked a character before the run starts.
                if (SceneObjects.InBreach)
                {
                    var lobby = FindObjectOfType<Lobby>();
                    foreach (var c in NetSession.Instance.Node.Connections)
                        if (lobby == null || !lobby.Roster.ContainsKey(c.Slot) || string.IsNullOrEmpty(lobby.Roster[c.Slot].Prefab))
                        {
                            Toasts.Show("Waiting for " + GungeonUI.WhoName(c) + " to pick a character", 4f, true);
                            GM.Field("m_loadingLevel").SetValue(false);
                            if (Pixelator.Instance) Pixelator.Instance.FadeToBlack(0.2f, true);
                            return false;
                        }
                }
                var resume = RunBackup.PendingResume;
                if (resume != null && SceneObjects.InBreach)
                {
                    // Resuming a saved run: jump straight to the saved floor with the saved seed.
                    if (gm.CurrentRunSeed != resume.Seed) gm.CurrentRunSeed = resume.Seed;
                    GM.Field("nextLevelIndex").SetValue(resume.FloorIndex);
                    Plugin.Log.LogInfo($"RunSync: resuming run at floor index {resume.FloorIndex}");
                }
                else if (!gm.IsSeeded)
                {
                    int seed = Random.Range(1, int.MaxValue);
                    gm.CurrentRunSeed = seed;
                    Plugin.Log.LogInfo("RunSync: run seed " + seed);
                }
                int index = GM.Field("nextLevelIndex").GetValue<int>();
                ReseedForFloor(gm.CurrentRunSeed, index);
                CurrentIndex = index;
                var w = new WireWriter(64);
                w.U8(MsgLoadLevel);
                w.I32(gm.CurrentRunSeed);
                w.I32(index);
                w.Str(gm.InjectedLevelName ?? "");
                Replication.WorldSync.Instance?.OnFloorUnloading();
                NetSession.Instance.Node.Broadcast((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
                NetSession.Instance.FlushNow();
                Plugin.Log.LogInfo($"RunSync: host loading level index {GM.Field("nextLevelIndex").GetValue<int>()}");
                return true;
            }
            if (clientAllowed) { clientAllowed = false; return true; }
            Plugin.Log.LogInfo("RunSync: client blocked its own level load (waiting for host)");
            return false;
        }

        /// <summary>
        /// Floor generation reseeded per floor, so floor N depends only on (run seed, N). A player who restarts the
        /// game mid-run and rejoins builds exactly the floor the host is on.
        /// </summary>
        static void ReseedForFloor(int seed, int index)
        {
            unchecked { BraveRandom.InitializeWithSeed(seed * 31 + index * 7919 + 17); }
        }

        /// <summary>Host: a player came back mid-run. Bring them to this floor (or just re-sync if they never left it).</summary>
        public void SendRejoin(Connection c)
        {
            if (!NetSession.IsHost || CurrentIndex < 0 || SceneObjects.InBreach) return;
            var gm = GameManager.Instance;
            var w = new WireWriter(96);
            w.U8(MsgRejoin);
            w.I32(gm.CurrentRunSeed);
            w.I32(CurrentIndex);
            w.Str(gm.InjectedLevelName ?? "");
            var lobby = FindObjectOfType<Lobby>();
            w.Str(lobby != null && lobby.Roster.TryGetValue(c.Slot, out var e) ? e.Prefab : "");
            c.Send((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
            Plugin.Log.LogInfo($"RunSync: sent rejoin (floor index {CurrentIndex}) to slot {c.Slot}");
        }

        void OnControl(Connection c, byte[] data, Delivery d)
        {
            var r = new WireReader(data, 0, data.Length);
            byte type = r.U8();
            if (NetSession.IsHost) return;
            if (type == MsgRejoin) { OnRejoin(ref r); return; }
            if (type == MsgLayout) { hostLayoutIndex = r.I32(); hostLayout = r.Str(); CheckLayout(); return; }
            if (type != MsgLoadLevel) return;
            int seed = r.I32();
            int index = r.I32();
            string injected = r.Str();
            // Still finding our way back after a restart (the game may not even be ready to load yet): go to this
            // floor when the rejoin gets there, not now. The host moved on while we were away.
            if (rejoining)
            {
                pendingSeed = seed; pendingIndex = index; pendingInjected = injected; hasPending = true;
                Plugin.Log.LogInfo($"RunSync: host moved to floor index {index} while we rejoin, going there instead");
                return;
            }
            LoadFloor(seed, index, injected);
        }

        static bool hasPending;
        static int pendingSeed, pendingIndex;
        static string pendingInjected;

        void OnRejoin(ref WireReader r)
        {
            int seed = r.I32();
            int index = r.I32();
            string injected = r.Str();
            string prefab = r.Str();
            var gm = GameManager.Instance;
            var ws = Replication.WorldSync.Instance;
            // Already on our way there (a load can take longer than the host's resend interval).
            if (rejoining || gm.IsLoadingLevel || Dungeon.IsGenerating) return;
            if (!SceneObjects.InBreach && gm.CurrentRunSeed == seed && CurrentIndex == index && ws != null && ws.FloorReady)
            {
                // Short drop: we never left this floor. Just ask the host for a full refresh.
                Plugin.Log.LogInfo("RunSync: rejoined on the same floor, refreshing");
                ws.ResendReady();
                return;
            }
            Plugin.Log.LogInfo($"RunSync: rejoining the run on floor index {index}");
            Toasts.Show("Rejoining your run...", 5f);
            rejoining = true;
            hasPending = false;
            StartCoroutine(RejoinFlow(seed, index, injected, prefab));
        }

        static bool rejoining;

        System.Collections.IEnumerator RejoinFlow(int seed, int index, string injected, string prefab)
        {
            // Back in the Breach after a restart: become the same character, then go straight to the host's floor.
            if (GameManager.Instance.PrimaryPlayer == null && !string.IsNullOrEmpty(prefab))
                yield return StartCoroutine(CharacterPicker.Pick(prefab));
            float giveUp = Time.realtimeSinceStartup + 20f;
            while (GameManager.Instance.PrimaryPlayer == null && Time.realtimeSinceStartup < giveUp) yield return null;
            if (hasPending) { seed = pendingSeed; index = pendingIndex; injected = pendingInjected; hasPending = false; }
            LoadFloor(seed, index, injected);
        }

        void LoadFloor(int seed, int index, string injected)
        {
            var gm = GameManager.Instance;
            if (gm.CurrentRunSeed != seed) gm.CurrentRunSeed = seed;
            ReseedForFloor(seed, index);
            CurrentIndex = index;
            GM.Field("nextLevelIndex").SetValue(index);
            gm.InjectedLevelName = injected;
            GM.Field("m_loadingLevel").SetValue(false);
            Plugin.Log.LogInfo($"RunSync: host says load index {index} seed {seed}");
            clientAllowed = true;
            Replication.WorldSync.Instance?.OnFloorUnloading();
            bool fromFoyer = Game.SceneObjects.InBreach;
            if (Pixelator.Instance) Pixelator.Instance.FadeToBlack(0.2f);
            gm.LoadNextLevel();
            if (fromFoyer) Game.SceneObjects.Breach.OnDepartedFoyer();
        }

        readonly System.Collections.Generic.Dictionary<Connection, float> lastRejoin = new System.Collections.Generic.Dictionary<Connection, float>();

        /// <summary>
        /// Host: anyone connected but not ready on this floor (joined while we were loading, came back after a
        /// crash, missed the load message) is sent the rejoin again until they are on the floor with us.
        /// </summary>
        Dungeon stragglerFloor;
        float stragglerFloorAt;

        void BringStragglers()
        {
            if (!NetSession.Active || !NetSession.IsHost || CurrentIndex < 0 || SceneObjects.InBreach) return;
            if (GameManager.Instance.IsLoadingLevel || GameManager.Instance.Dungeon == null || Dungeon.IsGenerating) return;
            var ws = Replication.WorldSync.Instance;
            if (ws == null) return;
            float now = Time.realtimeSinceStartup;
            // A friend on a slow PC can take a while to load the same floor: give them 40 s before re-sending it.
            if (GameManager.Instance.Dungeon != stragglerFloor) { stragglerFloor = GameManager.Instance.Dungeon; stragglerFloorAt = now; }
            if (now - stragglerFloorAt < 40f) return;
            foreach (var c in NetSession.Instance.Node.Connections)
            {
                if (ws.IsReady(c)) continue;
                if (lastRejoin.TryGetValue(c, out var t) && now - t < 40f) continue;
                lastRejoin[c] = now;
                SendRejoin(c);
            }
        }

        void Update()
        {
            BringStragglers();
            // Every floor is a new Dungeon object, but all floors load into the same scene, so key on the dungeon.
            if (!GameManager.HasInstance || GameManager.Instance.Dungeon == null || Dungeon.IsGenerating || GameManager.Instance.IsLoadingLevel) return;
            if (GameManager.Instance.Dungeon.data == null || GameManager.Instance.Dungeon.data.rooms == null) return;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "#" + GameManager.Instance.Dungeon.GetInstanceID();
            if (scene == lastScene) return;
            lastScene = scene;
            LastFingerprint = Fingerprint();
            fingerprintIndex = SceneObjects.InBreach ? -1 : CurrentIndex; // which floor that fingerprint is of
            // Not ready in the Breach while on our way back into a run: the host would send us its floor's world.
            if (NetSession.Active && !NetSession.IsHost && !(rejoining && SceneObjects.InBreach)) Replication.WorldSync.Instance?.OnFloorLoaded();
            if (!SceneObjects.InBreach) rejoining = false;
            if (NetSession.Active && NetSession.IsHost && !SceneObjects.InBreach) RunBackup.OnHostFloorReady();
            if (SceneObjects.InBreach) CurrentIndex = -1;
            Plugin.Log.LogInfo("FLOOR " + scene + " " + LastFingerprint);
            if (NetSession.Active && NetSession.IsHost && !SceneObjects.InBreach) SendLayout(null);
            if (NetSession.Active && !NetSession.IsHost) CheckLayout();
            var dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR");
            if (dir != null)
            {
                var lines = new System.Collections.Generic.List<string>();
                foreach (var e in StaticReferenceManager.AllEnemies)
                    if (e) lines.Add($"{e.EnemyGuid} {e.name} {e.transform.position.x:F2},{e.transform.position.y:F2} room={(e.ParentRoom != null ? e.ParentRoom.GetRoomName() : "-")}");
                lines.Sort();
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "enemies-" + scene + ".txt"), lines.ToArray());
            }
        }

        // ---------- Same floor on every machine? ----------
        // Every machine builds the floor itself from the host's seed. Another mod that changes floors (new rooms,
        // new enemies) makes each machine build a different one: rooms, walls and enemies stop matching. The host
        // sends its layout; a guest whose own differs is told why, once per floor.

        static string hostLayout;
        static int hostLayoutIndex = -2, warnedIndex = -2, fingerprintIndex = -1;

        static string LayoutPart(string fp)
        {
            int i = fp.IndexOf("rooms="), j = fp.IndexOf(" enemies=");
            return i >= 0 && j > i ? fp.Substring(i, j - i) : fp;
        }

        /// <summary>Host: our layout to everyone (or one newcomer).</summary>
        public void SendLayout(Connection to)
        {
            // Only a fingerprint of the floor we are on now (mid-load, the last one is still the previous floor's).
            if (!NetSession.Active || !NetSession.IsHost || fingerprintIndex < 0 || fingerprintIndex != CurrentIndex || LastFingerprint == "-") return;
            if (GameManager.Instance.IsLoadingLevel || Dungeon.IsGenerating) return;
            var w = new WireWriter(96);
            w.U8(MsgLayout); w.I32(fingerprintIndex); w.Str(LayoutPart(LastFingerprint));
            if (to != null) to.Send((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
            else NetSession.Instance.Node.Broadcast((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
        }

        void CheckLayout()
        {
            if (hostLayout == null || fingerprintIndex < 0 || hostLayoutIndex != fingerprintIndex || fingerprintIndex != CurrentIndex || SceneObjects.InBreach || LastFingerprint == "-") return;
            if (GameManager.Instance.IsLoadingLevel || Dungeon.IsGenerating) return;
            var mine = LayoutPart(LastFingerprint);
            if (System.Environment.GetEnvironmentVariable("GO_LAYOUT_TEST") == "1") mine += "-test"; // tests: a floor that differs
            if (mine == hostLayout || warnedIndex == CurrentIndex) return;
            warnedIndex = CurrentIndex;
            var others = OtherMods();
            Plugin.Log.LogWarning($"FLOOR MISMATCH: host {hostLayout}, mine {mine}; other mods: {(others.Length > 0 ? others : "none")}");
            Toasts.Show(others.Length > 0
                ? "This floor is different from the host's, so rooms and enemies won't match. Other mods can change floors: " + others + ". Everyone needs the same mods, or play without them."
                : "This floor is different from the host's, so rooms and enemies won't match. Does the host have other mods installed? Everyone needs the same mods.", 14f, true);
        }

        /// <summary>Other BepInEx plugins loaded next to this one, by name.</summary>
        public static string OtherMods()
        {
            var names = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                    if (kv.Key != Plugin.Guid && kv.Value != null && kv.Value.Metadata != null) names.Add(kv.Value.Metadata.Name);
            }
            catch (System.Exception) { }
            return string.Join(", ", names.ToArray());
        }

        /// <summary>Hashes of the floor layout, enemies, chests and pickups, with counts.</summary>
        public static string Fingerprint()
        {
            var d = GameManager.Instance.Dungeon;
            var layout = new StringBuilder();
            foreach (var room in d.data.rooms)
                layout.Append(room.area.basePosition).Append(room.area.dimensions).Append(room.GetRoomName()).Append('|');
            var enemies = new StringBuilder();
            int ne = 0;
            foreach (var e in StaticReferenceManager.AllEnemies)
            {
                if (!e) continue;
                ne++;
                var p = e.transform.position;
                enemies.Append(e.EnemyGuid).Append(Mathf.RoundToInt(p.x * 4)).Append(',').Append(Mathf.RoundToInt(p.y * 4)).Append('|');
            }
            var chests = new StringBuilder();
            int nc = 0;
            foreach (var ch in StaticReferenceManager.AllChests)
            {
                if (!ch) continue;
                nc++;
                chests.Append(ch.name).Append(ch.transform.position).Append('|');
            }
            int np = 0;
            var picks = new StringBuilder();
            foreach (var po in Object.FindObjectsOfType<PickupObject>())
            {
                if (!po || po.transform.parent != null) continue;
                np++;
                picks.Append(po.PickupObjectId).Append(po.transform.position).Append('|');
            }
            return $"seed={GameManager.Instance.CurrentRunSeed} rooms={d.data.rooms.Count} layout={H(layout)} enemies={ne}:{H(enemies)} chests={nc}:{H(chests)} pickups={np}:{H(picks)}";
        }

        static string H(StringBuilder sb)
        {
            uint h = 2166136261;
            var s = sb.ToString();
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return h.ToString("X8");
        }
    }
}
