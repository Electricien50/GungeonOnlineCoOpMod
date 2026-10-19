using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace GungeonOnline.Dev
{
    /// <summary>Cheap per-section timing of the mod's own work, averaged per frame over the last second.</summary>
    internal static class NetProfiler
    {
        sealed class Section { public long Ticks; public int Frames; public double LastMs; }
        static readonly Dictionary<string, Section> sections = new Dictionary<string, Section>();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static long windowStart;
        static int frames;

        public static long Begin() => clock.ElapsedTicks;

        public static void End(string name, long started)
        {
            if (!sections.TryGetValue(name, out var s)) sections[name] = s = new Section();
            s.Ticks += clock.ElapsedTicks - started;
        }

        /// <summary>Call once per frame.</summary>
        public static void Frame()
        {
            frames++;
            long now = clock.ElapsedTicks;
            if (now - windowStart < Stopwatch.Frequency) return;
            foreach (var s in sections.Values)
            {
                s.LastMs = frames > 0 ? s.Ticks * 1000.0 / Stopwatch.Frequency / frames : 0;
                s.Ticks = 0;
            }
            frames = 0;
            windowStart = now;
        }

        public static string Summary()
        {
            var sb = new StringBuilder();
            double total = 0;
            foreach (var kv in sections) { sb.Append(kv.Key).Append('=').Append(kv.Value.LastMs.ToString("F2")).Append(' '); total += kv.Value.LastMs; }
            return "modMsPerFrame=" + total.ToString("F2") + " " + sb;
        }
    }
}
