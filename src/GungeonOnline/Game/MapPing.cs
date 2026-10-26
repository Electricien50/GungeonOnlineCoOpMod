using System.Collections.Generic;
using GungeonOnline.Net;
using GungeonOnline.Replication;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Map pings. With the full map open, right click (mouse) or X / Square (controller, at the map crosshair)
    /// marks a spot. Everyone sees a pulsing ring in the pinger's colour on the minimap and the full map, and on
    /// screen when the spot is in view, for 8 seconds. The host passes each ping on to the other players.
    /// Purely visual: nothing in the game world changes, so a ping can never desync a run.
    /// GO_PING_AT=seconds (with GO_PING_WHO=host|guest): harness ping at the map's centre, logged as "MAPPING".
    /// </summary>
    [DefaultExecutionOrder(-200)]
    internal sealed class MapPing : MonoBehaviour
    {
        public const byte EvPing = 47; // routed with the 40-49 events
        const float Life = 8f, Cooldown = 0.35f;
        const int PerPlayer = 3;

        static MapPing instance;

        sealed class Ping
        {
            public int Slot;
            public Vector2 World;
            public float Born;
            public Color Color;
            public GameObject Marker;
            public Mesh Mesh;
        }

        readonly List<Ping> pings = new List<Ping>();
        readonly WireWriter w = new WireWriter(32);
        float lastSent = -10f;
        bool harnessDone;
        static Texture2D ring;
        public static int Sent, Received;

        void Awake() { instance = this; }

        void Start()
        {
            NetSession.Instance.SessionEnded += ClearAll;
        }

        static bool MapOpen => NetSession.Active && Minimap.HasInstance && Minimap.Instance && Minimap.Instance.IsFullscreen && !OnlineMenu.IsOpen;

        void Update()
        {
            HarnessTick();
            if (!MapOpen) return;
            var me = PlayerSlots.Local;
            if (!me) return;
            var input = BraveInput.GetInstanceForPlayer(me.PlayerIDX);
            if (input == null || input.ActiveActions == null) return;
            bool pressed = false;
            Vector2 screen;
            if (input.IsKeyboardAndMouse())
            {
                // Left click already pans the map and picks teleporters; right click would roll.
                input.ConsumeAll(GungeonActions.GungeonActionType.DodgeRoll);
                pressed = UnityEngine.Input.GetMouseButtonDown(1);
                screen = UnityEngine.Input.mousePosition;
            }
            else
            {
                // The map uses A (teleport), Y (drop), the right stick and the d-pad. X / Square is free; it would reload.
                input.ConsumeAll(GungeonActions.GungeonActionType.Reload);
                var dev = input.ActiveActions.Device;
                pressed = dev != null && dev.Action3.WasPressed;
                screen = new Vector2(Screen.width / 2f, Screen.height / 2f); // the map crosshair
            }
            if (pressed) PingAtScreen(screen);
        }

        void LateUpdate()
        {
            TickMarkers();
            if (!MapOpen) return;
            var me = PlayerSlots.Local;
            var input = me ? BraveInput.GetInstanceForPlayer(me.PlayerIDX) : null;
            if (input == null) return;
            input.ConsumeAll(input.IsKeyboardAndMouse() ? GungeonActions.GungeonActionType.DodgeRoll : GungeonActions.GungeonActionType.Reload);
        }

        /// <summary>Screen point on the full map to a dungeon position (the same sum the map uses for its icons).</summary>
        static bool ScreenToWorld(Vector2 screen, out Vector2 world)
        {
            world = default;
            var map = Minimap.Instance;
            if (!map || map.cameraRef == null) return false;
            Vector2 vp = BraveUtility.GetMinimapViewportPosition(screen);
            vp.x = (vp.x - 0.5f) * (BraveCameraUtility.ASPECT / 1.7777778f) + 0.5f;
            Vector2 mapPoint = map.cameraRef.ViewportPointToRay(vp).origin;
            world = (mapPoint - (Vector2)map.transform.position) * 8f;
            return !float.IsNaN(world.x) && !float.IsNaN(world.y);
        }

        public void PingAtScreen(Vector2 screen)
        {
            if (Time.realtimeSinceStartup - lastSent < Cooldown) return;
            if (!ScreenToWorld(screen, out var world)) return;
            var dungeon = GameManager.HasInstance ? GameManager.Instance.Dungeon : null;
            if (dungeon == null || dungeon.data == null || !dungeon.data.CheckInBounds(world.ToIntVector2(VectorConversions.Floor))) return;
            lastSent = Time.realtimeSinceStartup;
            Sent++;
            Add(NetSession.LocalSlot, world);
            Send(NetSession.LocalSlot, world, null);
            Plugin.Log.LogInfo($"MAPPING sent slot={NetSession.LocalSlot} at {world.x:F1},{world.y:F1}");
        }

        void Send(int slot, Vector2 world, Connection except)
        {
            w.Reset(); w.U8(EvPing); w.U8((byte)slot); w.F32(world.x); w.F32(world.y);
            var msg = w.ToArray();
            foreach (var c in NetSession.Instance.Node.Connections)
                if (c != except) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
        }

        /// <summary>Called by WorldSync's event router on both sides.</summary>
        public static void Apply(Connection from, ref WireReader r)
        {
            int slot = r.U8();
            var world = new Vector2(r.F32(), r.F32());
            if (instance == null) return;
            if (NetSession.IsHost) instance.Send(slot, world, from); // pass it on to everyone else
            if (slot == NetSession.LocalSlot) return;
            Received++;
            instance.Add(slot, world);
            if (PlayerSlots.Local) AkSoundEngine.PostEvent("Play_UI_cooldown_ready_01", PlayerSlots.Local.gameObject);
            Plugin.Log.LogInfo($"MAPPING got slot={slot} at {world.x:F1},{world.y:F1}");
        }

        void Add(int slot, Vector2 world)
        {
            int mine = 0;
            for (int i = pings.Count - 1; i >= 0; i--)
                if (pings[i].Slot == slot && ++mine >= PerPlayer) Remove(i);
            var p = PlayerSlots.ForSlot(slot);
            var color = p ? PartnerUI.ColorFor(p) : Color.white;
            pings.Add(new Ping { Slot = slot, World = world, Born = Time.realtimeSinceStartup, Color = color });
        }

        void Remove(int i)
        {
            var p = pings[i];
            if (p.Marker) Destroy(p.Marker);
            if (p.Mesh) Destroy(p.Mesh);
            pings.RemoveAt(i);
        }

        void ClearAll() { for (int i = pings.Count - 1; i >= 0; i--) Remove(i); }

        // ---------- Look ----------

        static Texture2D Ring()
        {
            if (ring) return ring;
            const int n = 32;
            ring = new Texture2D(n, n, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            float c = (n - 1) / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    bool on = (d > 11.5f && d < 15.5f) || d < 4.5f; // ring plus centre dot
                    bool edge = (d > 10.5f && d <= 11.5f) || (d >= 15.5f && d < 16.5f) || (d >= 4.5f && d < 5.5f);
                    px[y * n + x] = on ? Color.white : edge ? new Color(0f, 0f, 0f, 0.8f) : new Color(0f, 0f, 0f, 0f);
                }
            ring.SetPixels(px);
            ring.Apply();
            return ring;
        }

        /// <summary>A ring inside the minimap itself, so it shows on the corner map and the full map alike.</summary>
        void MakeMarker(Ping p)
        {
            var map = Minimap.Instance;
            var me = PlayerSlots.Local;
            if (!map || !me || !me.minimapIconPrefab) return;
            var refRenderer = me.minimapIconPrefab.GetComponent<Renderer>();
            if (refRenderer == null || refRenderer.sharedMaterial == null) return;
            var go = new GameObject("GO_MapPing");
            go.layer = me.minimapIconPrefab.layer;
            go.transform.parent = map.transform;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var mat = new Material(refRenderer.sharedMaterial) { mainTexture = Ring() };
            mr.sharedMaterial = mat;
            mr.sortingLayerName = "Foreground";
            mr.sortingOrder = 50;
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(0.5f, 0.5f), new Vector3(-0.5f, 0.5f) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.colors = new[] { p.Color, p.Color, p.Color, p.Color };
            mf.sharedMesh = mesh;
            p.Marker = go;
            p.Mesh = mesh;
        }

        static float Pulse(float age) => 1f + 0.35f * Mathf.Abs(Mathf.Sin(age * 5f)) * Mathf.Clamp01(1.5f - age * 0.5f);
        static float Alpha(float age) => Mathf.Clamp01((Life - age) / 1.5f);

        void TickMarkers()
        {
            float now = Time.realtimeSinceStartup;
            var map = Minimap.HasInstance ? Minimap.Instance : null;
            for (int i = pings.Count - 1; i >= 0; i--)
            {
                var p = pings[i];
                float age = now - p.Born;
                if (age > Life || !NetSession.Active) { Remove(i); continue; }
                if (!map) continue;
                if (!p.Marker) MakeMarker(p);
                if (!p.Marker) continue;
                if (p.Marker.transform.parent != map.transform) p.Marker.transform.parent = map.transform;
                p.Marker.transform.position = map.transform.position + new Vector3(p.World.x * 0.125f, p.World.y * 0.125f, -1.5f);
                // Bigger on the full map's zoomed-out view, readable on the corner map.
                float size = (map.IsFullscreen ? 0.9f : 0.6f) * Pulse(age);
                p.Marker.transform.localScale = new Vector3(size, size, 1f);
                var c = p.Color; c.a = Alpha(age);
                p.Mesh.colors = new[] { c, c, c, c };
            }
        }

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || pings.Count == 0 || !NetSession.Active || !GungeonUI.Ready) return;
            if (OnlineMenu.IsOpen || MapOpen || !GameManager.HasInstance) return;
            var gm = GameManager.Instance;
            if (gm.IsLoadingLevel || gm.IsPaused || gm.MainCameraController == null) return;
            var cam = gm.MainCameraController.GetComponent<UnityEngine.Camera>();
            if (cam == null) return;
            var pr = cam.pixelRect;
            var view = new Rect(pr.x, Screen.height - pr.yMax, pr.width, pr.height);
            float now = Time.realtimeSinceStartup;
            int px = Mathf.Max(1, GungeonUI.Px);
            var old = GUI.color;
            foreach (var p in pings)
            {
                Vector3 vp = cam.WorldToViewportPoint(new Vector3(p.World.x, p.World.y, 0f));
                if (vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) continue;
                float age = now - p.Born;
                var at = new Vector2(view.x + vp.x * view.width, view.y + (1f - vp.y) * view.height);
                float s = 16f * px * Pulse(age);
                var c = p.Color; c.a = Alpha(age);
                GUI.color = c;
                GUI.DrawTexture(new Rect(at.x - s / 2f, at.y - s / 2f, s, s), Ring());
            }
            GUI.color = old;
        }

        // ---------- Harness ----------

        void HarnessTick()
        {
            if (harnessDone || !Dev.DevEnv.IsHarness || !NetSession.Active || !GameManager.HasInstance) return;
            if (!float.TryParse(System.Environment.GetEnvironmentVariable("GO_PING_AT"), out var at)) { harnessDone = true; return; }
            string who = System.Environment.GetEnvironmentVariable("GO_PING_WHO") ?? "guest";
            if (who != (NetSession.IsHost ? "host" : "guest")) { harnessDone = true; return; }
            float t = Time.realtimeSinceStartup;
            if (t < at || SceneObjects.InBreach || GameManager.Instance.IsLoadingLevel || !Minimap.HasInstance || !PlayerSlots.Local) return;
            if (!Minimap.Instance.IsFullscreen) { Minimap.Instance.ToggleMinimap(true, true); return; }
            harnessDone = true;
            var me = PlayerSlots.Local.CenterPosition;
            Plugin.Log.LogInfo($"MAPPING harness: my player at {me.x:F1},{me.y:F1}");
            lastSent = -10f;
            PingAtScreen(new Vector2(Screen.width / 2f, Screen.height / 2f));
            StartCoroutine(CloseMapSoon());
        }

        System.Collections.IEnumerator CloseMapSoon()
        {
            yield return new WaitForSecondsRealtime(3f);
            if (Minimap.HasInstance && Minimap.Instance) Minimap.Instance.ToggleMinimap(false);
        }
    }
}
