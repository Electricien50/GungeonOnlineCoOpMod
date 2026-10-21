using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// metrics.jsonl: every 5 seconds of a session, one line of connection and sync numbers (ping, jitter, loss,
    /// input buffer, corrections, desyncs, fps). For tuning after a real session; included in "Save report".
    /// Kept under 5 MB (older lines move to metrics.old.jsonl).
    /// </summary>
    internal sealed class Metrics : MonoBehaviour
    {
        public static string FilePath => Path.Combine(System.Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? Path.Combine(BepInEx.Paths.BepInExRootPath, "GungeonOnline"), "metrics.jsonl");
        float next;
        int frames;
        float frameTime, worstFrame;

        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        void Update()
        {
            frames++;
            frameTime += Time.unscaledDeltaTime;
            if (Time.unscaledDeltaTime > worstFrame) worstFrame = Time.unscaledDeltaTime;
            if (!NetSession.Active || Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 5f;
            var sb = new StringBuilder(256);
            sb.Append("{\"t\":\"").Append(System.DateTime.Now.ToString("s")).Append("\",\"role\":\"").Append(NetSession.IsHost ? "host" : "guest").Append('"');
            sb.Append(",\"fps\":").Append(F(frameTime > 0 ? frames / frameTime : 0));
            sb.Append(",\"worstFrameMs\":").Append(F(worstFrame * 1000f)).Append(",\"slides\":").Append(Replication.DrawSmoothing.Slides);
            frames = 0; frameTime = 0; worstFrame = 0;
            sb.Append(",\"floor\":\"").Append(Game.SceneObjects.InBreach ? "breach" : (GameManager.HasInstance && GameManager.Instance.Dungeon != null ? GameManager.Instance.Dungeon.DungeonShortName : "-")).Append('"');
            sb.Append(",\"peers\":[");
            bool first = true;
            foreach (var c in NetSession.Instance.Node.Connections)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"slot\":").Append(c.Slot).Append(",\"ping\":").Append(F(c.RttMs)).Append(",\"jitter\":").Append(F(c.RttVarMs))
                  .Append(",\"loss\":").Append(F(c.LossPercent)).Append(",\"resends\":").Append(c.Resends)
                  .Append(",\"via\":\"").Append(c.Remote is SteamEndPoint ? "steam" : "udp").Append('"');
                var ins = Replication.InputSync.Instance;
                if (ins != null && ins.Streams.TryGetValue(c.Slot, out var st))
                    sb.Append(",\"buffer\":").Append(F(st.BufferMs)).Append(",\"inJitter\":").Append(F(st.JitterMs)).Append(",\"starved\":").Append(st.Starved).Append(",\"held\":").Append(st.Held).Append(",\"sendGap\":").Append(F(st.SendGapMs))
                      .Append(",\"applied\":").Append(st.Applied).Append(",\"merged\":").Append(st.Merged)
                      .Append(",\"direct\":").Append(st.FreshDirect).Append(",\"relayed\":").Append(st.FreshRelay).Append(",\"pollLag\":").Append(F(st.PollLagMs));
                sb.Append('}');
            }
            sb.Append(']');
            var ps = Replication.PlayerSync.Instance;
            if (ps != null) sb.Append(",\"localSync\":\"").Append(ps.Stats(NetSession.LocalSlot)).Append('"');
            var ck = Replication.Checksum.Instance;
            if (ck != null) sb.Append(",\"checks\":").Append(ck.Checks).Append(",\"desyncs\":").Append(ck.Mismatches);
            sb.Append("}\n");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    string old = Path.Combine(fi.DirectoryName, "metrics.old.jsonl");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                File.AppendAllText(FilePath, sb.ToString());
            }
            catch { }
        }
    }
}
