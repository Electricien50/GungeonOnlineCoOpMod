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

        float floorSince = -1f;
        string floorScene;

        /// <summary>Harness host (GO_FLOOR_EVERY=seconds): move on to the next floor on a timer, re-run the fight setup on each.</summary>
        void HostFloorCycle(float t)
        {
            var v = System.Environment.GetEnvironmentVariable("GO_FLOOR_EVERY");
            if (v == null || !Session.NetSession.IsHost || Foyer.Instance != null || GameManager.Instance.IsLoadingLevel) return;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + GameManager.Instance.CurrentFloor;
            if (scene != floorScene) { floorScene = scene; floorSince = t; fightDone = false; fightAt = -1f; return; }
            if (!float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var every)) return;
            if (t - floorSince < every) return;
            floorSince = t + 1000f;
            Plugin.Log.LogInfo($"Harness: next floor (was floor {GameManager.Instance.CurrentFloor})");
            GameManager.Instance.DelayedLoadNextLevel(0.5f);
        }

        float buyAt = -1f;
        bool bought;

        /// <summary>Harness client (GO_TEST_BUY=1): buy the nearest shop item once, through the normal request path.</summary>
        void ClientTestBuy(float t)
        {
            if (bought || System.Environment.GetEnvironmentVariable("GO_TEST_BUY") != "1") return;
            if (!Session.NetSession.Active || Session.NetSession.IsHost) return;
            var me = GameManager.Instance.PrimaryPlayer;
            if (me == null || Foyer.Instance != null) return;
            ShopItemController best = null;
            float bestD = 36f;
            foreach (var si in FindObjectsOfType<ShopItemController>())
            {
                if (!si || !si.item || si.Acquired) continue;
                float d = ((Vector2)si.transform.position - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; best = si; }
            }
            if (best == null) return;
            if (buyAt < 0) { buyAt = t + 5f; return; }
            if (t < buyAt) return;
            bought = true;
            Plugin.Log.LogInfo($"Harness: client buying item {best.item.PickupObjectId} for {best.CurrentPrice}");
            best.Interact(me);
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
            if (mode == "shop")
            {
                ShopItemController any = null;
                foreach (var si in Game.SceneObjects.All<ShopItemController>()) if (si && si.item && !si.Acquired) { any = si; Plugin.Log.LogInfo($"Harness: shop item {si.item.PickupObjectId} acquired={si.Acquired} price={si.CurrentPrice}"); break; }
                if (any == null)
                {
                    // Shop stock can appear a little after the floor; keep looking for 20 s.
                    if (t - fightAt < 20f) { fightDone = false; return; }
                    Plugin.Log.LogWarning("Harness: no shop on this floor");
                    return;
                }
                int k = 0;
                foreach (var p in gm.AllPlayers)
                {
                    if (!p) continue;
                    p.healthHaver.IsVulnerable = false;
                    p.carriedConsumables.Currency = 300;
                    p.WarpToPoint((Vector2)any.transform.position + new Vector2(k * 1.5f - 0.75f, -2.5f));
                    k++;
                }
                Plugin.Log.LogInfo("Harness: warped players into the shop with 300 casings");
                return;
            }
            var rooms = new System.Collections.Generic.List<Dungeonator.RoomHandler>();
            foreach (var room in gm.Dungeon.data.rooms)
            {
                var list = room.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear);
                if (list != null && list.Count >= 2 && room != gm.Dungeon.data.Entrance) rooms.Add(room);
            }
            rooms.Sort((x, y) => y.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear).Count.CompareTo(x.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear).Count));
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
            NetProfiler.Frame();
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
                    if (ws != null) net += $" world[ready={ws.ReadyPeers} tracked={ws.TrackedCount} replicas={ws.ReplicaCount} spawnsSent={ws.SpawnsSent} spawnsApplied={ws.SpawnsApplied} deathsSent={ws.DeathsSent} deathsApplied={ws.DeathsApplied} snaps={ws.SnapshotsApplied} localSuppressed={ws.LocalActorsSuppressed} spawnFail={ws.SpawnFailures} enemies={StaticReferenceManager.AllEnemies.Count}]";
                    var ps = Replication.ProjectileSync.Instance;
                    if (ps != null) net += $" proj[announced={ps.Announced} updates={ps.Updates} copies={ps.Copies} suppressed={ps.LocalSuppressed} unknown={ps.UnknownPrefabs}]";
                    var pk = Replication.PickupSync.Instance;
                    if (pk != null) net += $" pick[ann={pk.PickupsAnnounced} copies={pk.PickupsCopied} purged={pk.LocalPurged} req={pk.Requests} gives={pk.Gives} givesApplied={pk.GivesApplied} chests={pk.ChestsMapped} chestOpened={pk.ChestsOpened}]";
                    var ck = Replication.Checksum.Instance;
                    if (ck != null) net += $" check[summaries={ck.Summaries} checks={ck.Checks} mismatches={ck.Mismatches} resyncs={ck.Resyncs} clean={ck.ConsecutiveClean}]";
                    var sh = Replication.ShopSync.Instance;
                    if (sh != null) net += $" shop[ann={sh.Announced} mapped={sh.Mapped} restocked={sh.Restocked} req={sh.Requests}]";
                    var inv = Replication.InventorySync.Instance;
                    if (inv != null) net += $" inv[sent={inv.Sent} applied={inv.Applied} added={inv.Added} removed={inv.Removed}]";
                    var vs = Replication.VitalsSync.Instance;
                    if (vs != null) net += $" vitals[hurtSent={vs.HurtsSent} hurtApplied={vs.HurtsApplied} sent={vs.VitalsSent} applied={vs.VitalsApplied}]";
                    foreach (var kv in Game.PlayerSlots.Remotes) if (kv.Value) net += $" hpR{kv.Key}={kv.Value.healthHaver.GetCurrentHealth()}";
                    if (gm.PrimaryPlayer != null) net += $" money={gm.PrimaryPlayer.carriedConsumables.Currency} guns={gm.PrimaryPlayer.inventory.AllGuns.Count} passives={gm.PrimaryPlayer.passiveItems.Count}";
                    foreach (var kv in Game.PlayerSlots.Remotes) if (kv.Value) net += $" R{kv.Key}guns={kv.Value.inventory.AllGuns.Count} R{kv.Key}passives={kv.Value.passiveItems.Count}";
                    net += $" hp0={(gm.PrimaryPlayer != null ? gm.PrimaryPlayer.healthHaver.GetCurrentHealth() : -1)}";
                    net += $" syncLocal[{Replication.PlayerSync.Instance.Stats(Session.NetSession.LocalSlot)}] snaps={Replication.PlayerSync.Instance.SnapshotsApplied}";
                }
                string fp = Game.RunSync.Instance != null ? Game.RunSync.Instance.LastFingerprint : "-";
                string line = $"t={t:F1} frame={Time.frameCount} fps={1f / Mathf.Max(0.0001f, Time.smoothDeltaTime):F0} {NetProfiler.Summary()} scene={scene} floor=[{fp}] gm={gm != null} players={players} p0={pos} net={net}";
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
            HostFloorCycle(t);
            HostFightSetup(t);
            ClientTestBuy(t);
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
