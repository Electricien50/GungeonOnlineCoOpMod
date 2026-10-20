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
        const byte MsgLoadLevel = 1, MsgRejoin = 2;
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
            if (type != MsgLoadLevel) return;
            int seed = r.I32();
            int index = r.I32();
            string injected = r.Str();
            LoadFloor(seed, index, injected);
        }

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
            if (NetSession.Active && !NetSession.IsHost) Replication.WorldSync.Instance?.OnFloorLoaded();
            if (!SceneObjects.InBreach) rejoining = false;
            if (NetSession.Active && NetSession.IsHost && !SceneObjects.InBreach) RunBackup.OnHostFloorReady();
            if (SceneObjects.InBreach) CurrentIndex = -1;
            Plugin.Log.LogInfo("FLOOR " + scene + " " + LastFingerprint);
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
