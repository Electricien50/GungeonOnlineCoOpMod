using System.IO;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>Harness heartbeat and timed quit. Inert for players.</summary>
    internal sealed class DevRunner : MonoBehaviour
    {
        float nextStatus;
        bool suspended;
        float bothSince = -1f;

        /// <summary>Test instances are always silent: Wwise volumes forced to 0 every frame, engine suspended once it is up.</summary>
        void Mute()
        {
            AudioListener.volume = 0f;
            try
            {
                AkSoundEngine.SetRTPCValue("VOL_MUS", 0f);
                AkSoundEngine.SetRTPCValue("VOL_SFX", 0f);
                AkSoundEngine.SetRTPCValue("VOL_UI", 0f);
                if (!suspended && AkSoundEngine.IsInitialized())
                {
                    suspended = AkSoundEngine.Suspend() == AKRESULT.AK_Success;
                    if (suspended) Plugin.Log.LogInfo("Harness: audio engine suspended (muted)");
                }
            }
            catch { }
        }

        System.Collections.Generic.List<float> shots;

        /// <summary>GO_SHOTS="30,45": screenshots at those seconds since boot, saved as shot-30.png etc.</summary>
        void TimedShots(float t)
        {
            if (shots == null)
            {
                shots = new System.Collections.Generic.List<float>();
                var v = System.Environment.GetEnvironmentVariable("GO_SHOTS");
                if (v != null) foreach (var x in v.Split(',')) if (float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) shots.Add(f);
            }
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                if (t < shots[i]) continue;
                var dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "shot-" + Mathf.RoundToInt(shots[i]) + ".png"));
                Plugin.Log.LogInfo("Harness: screenshot at " + shots[i]);
                shots.RemoveAt(i);
            }
        }

        float fightAt = -1f;
        bool fightDone;

        /// <summary>
        /// Harness host only (GO_FIGHT=together|split): once the client is ready on floor 1, make players
        /// invulnerable and warp them into rooms that still have enemies.
        /// </summary>
        void HostFightSetup(float t)
        {
            string mode = System.Environment.GetEnvironmentVariable("GO_FIGHT");
            if (fightDone || mode == null || !Session.NetSession.IsHost) return;
            var ws = Replication.WorldSync.Instance;
            var gm = GameManager.Instance;
            if (ws == null || ws.ReadyPeers == 0 || gm.Dungeon == null || Foyer.Instance != null) return;
            if (fightAt < 0) { fightAt = t + 3f; return; }
            if (t < fightAt) return;
            fightDone = true;
            var rooms = new System.Collections.Generic.List<Dungeonator.RoomHandler>();
            foreach (var room in gm.Dungeon.data.rooms)
            {
                var list = room.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear);
                if (list != null && list.Count >= 2 && room != gm.Dungeon.data.Entrance) rooms.Add(room);
            }
            rooms.Sort((x, y) => x.distanceFromEntrance.CompareTo(y.distanceFromEntrance));
            if (rooms.Count == 0) { Plugin.Log.LogWarning("Harness: no fight room"); return; }
            int i = 0;
            foreach (var p in gm.AllPlayers)
            {
                if (!p) continue;
                p.healthHaver.IsVulnerable = false;
                var room = rooms[Mathf.Min(mode == "split" ? i : 0, rooms.Count - 1)];
                var spot = room.GetCenteredVisibleClearSpot(2, 2);
                p.WarpToPoint(spot.ToVector2() + new Vector2(0.5f + i, 0.5f));
                Plugin.Log.LogInfo($"Harness: warped player {p.PlayerIDX} into {room.GetRoomName()} ({room.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear).Count} enemies)");
                i++;
            }
        }

        void Awake()
        {
            if (!DevEnv.IsHarness) return;
            Mute();
            // Run at a chosen display rate (GO_FPS, default 60) instead of whatever the hidden desktop allows.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = DevEnv.Fps;
        }

        void Update()
        {
            if (!DevEnv.IsHarness) return;
            float t = Time.realtimeSinceStartup;
            Mute();
            if (Application.targetFrameRate != DevEnv.Fps || QualitySettings.vSyncCount != 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = DevEnv.Fps; }
            if (DevEnv.StatusFile != null && t >= nextStatus)
            {
                nextStatus = t + 0.5f;
                var gm = GameManager.HasInstance ? GameManager.Instance : null;
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                int players = gm != null && gm.AllPlayers != null ? gm.AllPlayers.Length : 0;
                string pos = gm != null && gm.PrimaryPlayer != null ? gm.PrimaryPlayer.transform.position.ToString("F2") : "-";
                string net = "off";
                var s = Session.NetSession.Instance;
                if (Session.NetSession.Active)
                {
                    net = $"{(Session.NetSession.IsHost ? "host" : "client")} slot={Session.NetSession.LocalSlot} peers={s.Node.Connections.Count}";
                    foreach (var c in s.Node.Connections) net += $" [P{c.Slot + 1} rtt={c.RttMs:F1} loss={c.LossPercent:F1} sent={c.PacketsSent} recv={c.PacketsReceived}]";
                    if (s.InviteCodeText != null) net += " code=" + s.InviteCodeText;
                    foreach (var kv in Game.PlayerSlots.Remotes)
                    {
                        string rp = kv.Value ? kv.Value.transform.position.ToString("F2") : "dead";
                        net += $" remote{kv.Key}={rp}";
                        if (Replication.InputSync.Instance.Streams.TryGetValue(kv.Key, out var st))
                            net += $" in{kv.Key}[applied={st.Applied} starved={st.Starved} merged={st.Merged} buf={st.BufferMs:F1}ms jit={st.JitterMs:F1}ms late={st.LastLateMs:F1}ms last={st.Last}]";
                        if (kv.Value)
                        {
                            var bi = BraveInput.GetInstanceForPlayer(kv.Value.PlayerIDX);
                            var act = bi != null ? bi.ActiveActions : null;
                            var ob = HarmonyLib.Traverse.Create(kv.Value).Field("m_disableInput").Field("m_overrides").GetValue<System.Collections.IList>();
                            string keys = "";
                            if (ob != null) foreach (var o in ob) keys += HarmonyLib.Traverse.Create(o).Field("key").GetValue<string>() + ";";
                            net += $" p{kv.Key}overrides=[{keys}]";
                            net += $" p{kv.Key}state={kv.Value.CurrentInputState} acts={(act != null ? act.Move.Vector.ToString() + " dev=" + (act.Device != null ? act.Device.Name : "null") + " off=" + act.ForceDisable + " en=" + act.Enabled : "null")}";
                        }
                        net += $" sync{kv.Key}[{Replication.PlayerSync.Instance.Stats(kv.Key)}]";
                    }
                    var lb = BraveInput.GetInstanceForPlayer(0);
                    if (lb != null && lb.ActiveActions != null) net += $" localMove={lb.ActiveActions.Move.Vector} localSeq={Replication.InputSync.Instance.LocalSeq}";
                    var ws = Replication.WorldSync.Instance;
                    if (ws != null) net += $" world[ready={ws.ReadyPeers} tracked={ws.TrackedCount} replicas={ws.ReplicaCount} spawnsSent={ws.SpawnsSent} spawnsApplied={ws.SpawnsApplied} deathsSent={ws.DeathsSent} deathsApplied={ws.DeathsApplied} snaps={ws.SnapshotsApplied} enemies={StaticReferenceManager.AllEnemies.Count}]";
                    net += $" syncLocal[{Replication.PlayerSync.Instance.Stats(Session.NetSession.LocalSlot)}] snaps={Replication.PlayerSync.Instance.SnapshotsApplied}";
                }
                string fp = Game.RunSync.Instance != null ? Game.RunSync.Instance.LastFingerprint : "-";
                string line = $"t={t:F1} frame={Time.frameCount} scene={scene} floor=[{fp}] gm={gm != null} players={players} p0={pos} net={net}";
                try { File.WriteAllText(DevEnv.StatusFile, line); } catch { }
                if (Session.NetSession.Active && gm != null && gm.PrimaryPlayer != null)
                {
                    string tr = $"TRACE t={t:F1} local={gm.PrimaryPlayer.transform.position.x:F2},{gm.PrimaryPlayer.transform.position.y:F2} st={gm.PrimaryPlayer.CurrentInputState}";
                    foreach (var kv in Game.PlayerSlots.Remotes)
                        if (kv.Value) tr += $" r{kv.Key}={kv.Value.transform.position.x:F2},{kv.Value.transform.position.y:F2} st={kv.Value.CurrentInputState} mv={BraveInput.GetInstanceForPlayer(kv.Value.PlayerIDX).ActiveActions.Move.Vector}";
                    Plugin.Log.LogInfo(tr);
                }
            }
            if (DevEnv.StartRunAt >= 0 && Session.NetSession.IsHost && Foyer.Instance != null && GameManager.Instance.AllPlayers.Length >= 2)
            {
                if (bothSince < 0) bothSince = t;
                if (t - bothSince >= DevEnv.StartRunAt)
                {
                    DevEnv.StartRunAt = -1f;
                    Plugin.Log.LogInfo("Harness: host leaving the Breach");
                    Pixelator.Instance.FadeToBlack(0.5f);
                    GameManager.Instance.DelayedLoadNextLevel(0.75f);
                    Foyer.Instance.OnDepartedFoyer();
                }
            }
            HostFightSetup(t);
            TimedShots(t);
            if (DevEnv.QuitAfter > 0 && t >= DevEnv.QuitAfter)
            {
                Plugin.Log.LogInfo("GO_QUIT_AFTER reached, quitting.");
                DevEnv.QuitAfter = -1;
                Application.Quit();
            }
        }
    }
}
