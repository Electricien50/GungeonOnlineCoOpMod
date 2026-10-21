using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// When a correction from the network lands (a friend's late input, an enemy update after a gap), the object is put
    /// exactly where it really is, which can be a small hop on screen. This draws it where it was and slides it the
    /// rest of the way over about a tenth of a second. Only the drawing moves: the object is shifted just while the
    /// main camera renders and put back right after, so hits, pickups, AI and checks always see the exact position.
    /// </summary>
    public static class DrawSmoothing
    {
        sealed class Entry { public Transform T; public Vector3 Off, Applied; public bool On; }
        static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        static readonly List<int> gone = new List<int>();
        public const float MaxHop = 1.5f;      // bigger than this is a real move (a warp): no slide
        const float HalfLife = 0.045f;
        static bool hooked;
        public static bool Disabled = System.Environment.GetEnvironmentVariable("GO_NO_SMOOTH") == "1"; // tests: the old look
        public static int Slides, Skipped;

        /// <summary>The object was just moved from <paramref name="drawnAt"/> to its exact position by a correction.</summary>
        public static void Corrected(Component c, Vector2 drawnAt, Vector2 exact)
        {
            if (Disabled || !c || !Session.NetConfig.SmoothFriends.Value) return;
            Hook();
            int id = c.GetInstanceID();
            if (!entries.TryGetValue(id, out var e)) entries[id] = e = new Entry { T = c.transform };
            var off = e.Off + (Vector3)(drawnAt - exact);
            if ((drawnAt - exact).magnitude < MaxHop && off.magnitude < MaxHop) { e.Off = off; Slides++; }
            else { e.Off = Vector3.zero; Skipped++; }
        }

        /// <summary>Forget any slide (a fall, a cart, a warp, a floor load).</summary>
        public static void Clear(Component c)
        {
            if (c && entries.TryGetValue(c.GetInstanceID(), out var e)) e.Off = Vector3.zero;
        }

        // How smooth a friend looks: change of on-screen step from one frame to the next (0 = perfectly even motion).
        sealed class Track { public Vector2 A, B; public int N; }
        static readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        public static double JerkSum; public static int JerkFrames; public static float JerkMax;

        /// <summary>Once a frame, after <see cref="Tick"/>: where this friend will be drawn.</summary>
        public static void Measure(Component c)
        {
            if (!c) return;
            int id = c.GetInstanceID();
            Vector2 drawn = c.transform.position;
            if (entries.TryGetValue(id, out var e)) drawn += (Vector2)e.Off;
            if (!tracks.TryGetValue(id, out var t)) tracks[id] = t = new Track();
            if (t.N >= 2)
            {
                Vector2 step = drawn - t.B, last = t.B - t.A;
                if (step.magnitude < MaxHop && last.magnitude < MaxHop && Time.unscaledDeltaTime < 0.05f)
                {
                    float j = (step - last).magnitude;
                    JerkSum += j; JerkFrames++; if (j > JerkMax) JerkMax = j;
                }
            }
            t.A = t.B; t.B = drawn; t.N++;
        }

        public static string Stats() => $"slides={Slides} skipped={Skipped} jerk={(JerkFrames > 0 ? JerkSum / JerkFrames : 0):F4}/{JerkMax:F3}";

        /// <summary>End of each frame: ease every slide toward zero.</summary>
        public static void Tick()
        {
            if (entries.Count == 0) return;
            TakeOff(); // a frame that was not drawn
            float k = Mathf.Pow(0.5f, Time.unscaledDeltaTime / HalfLife);
            gone.Clear();
            foreach (var kv in entries)
            {
                var e = kv.Value;
                if (!e.T) { gone.Add(kv.Key); continue; }
                e.Off *= k;
                if (e.Off.sqrMagnitude < 0.0004f) e.Off = Vector3.zero;
            }
            foreach (var g in gone) entries.Remove(g);
        }

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            UnityEngine.Camera.onPreCull += cam => { if (cam == UnityEngine.Camera.main) PutOn(); };
            UnityEngine.Camera.onPostRender += cam => { if (cam == UnityEngine.Camera.main) TakeOff(); };
        }

        static void PutOn()
        {
            foreach (var e in entries.Values)
                if (e.T && !e.On && e.Off != Vector3.zero) { e.T.position += e.Off; e.Applied = e.Off; e.On = true; }
        }

        static void TakeOff()
        {
            foreach (var e in entries.Values)
                if (e.On) { if (e.T) e.T.position -= e.Applied; e.On = false; }
        }
    }
}
