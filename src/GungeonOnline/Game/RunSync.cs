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
        const byte MsgLoadLevel = 1;
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
                if (!gm.IsSeeded)
                {
                    int seed = Random.Range(1, int.MaxValue);
                    gm.CurrentRunSeed = seed;
                    Plugin.Log.LogInfo("RunSync: run seed " + seed);
                }
                var w = new WireWriter(64);
                w.U8(MsgLoadLevel);
                w.I32(gm.CurrentRunSeed);
                w.I32(GM.Field("nextLevelIndex").GetValue<int>());
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

        void OnControl(Connection c, byte[] data, Delivery d)
        {
            var r = new WireReader(data, 0, data.Length);
            if (r.U8() != MsgLoadLevel || NetSession.IsHost) return;
            int seed = r.I32();
            int index = r.I32();
            string injected = r.Str();
            var gm = GameManager.Instance;
            if (gm.CurrentRunSeed != seed) gm.CurrentRunSeed = seed;
            GM.Field("nextLevelIndex").SetValue(index);
            gm.InjectedLevelName = injected;
            GM.Field("m_loadingLevel").SetValue(false);
            Plugin.Log.LogInfo($"RunSync: host says load index {index} seed {seed}");
            clientAllowed = true;
            Replication.WorldSync.Instance?.OnFloorUnloading();
            bool fromFoyer = Foyer.Instance != null;
            if (Pixelator.Instance) Pixelator.Instance.FadeToBlack(0.2f);
            gm.LoadNextLevel();
            if (fromFoyer) Foyer.Instance.OnDepartedFoyer();
        }

        void Update()
        {
            // Every floor is a new Dungeon object, but all floors load into the same scene, so key on the dungeon.
            if (!GameManager.HasInstance || GameManager.Instance.Dungeon == null || Dungeon.IsGenerating || GameManager.Instance.IsLoadingLevel) return;
            if (GameManager.Instance.Dungeon.data == null || GameManager.Instance.Dungeon.data.rooms == null) return;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "#" + GameManager.Instance.Dungeon.GetInstanceID();
            if (scene == lastScene) return;
            lastScene = scene;
            LastFingerprint = Fingerprint();
            if (NetSession.Active && !NetSession.IsHost) Replication.WorldSync.Instance?.OnFloorLoaded();
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
