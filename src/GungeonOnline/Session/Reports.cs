using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// "Save report": one text file on the Desktop with everything needed to look into a problem afterwards
    /// (mod log, desync log, settings, connection numbers). No zip library on this runtime, so it is one file.
    /// </summary>
    internal static class Reports
    {
        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Enter The Gungeon Online Co-Op Mod report " + DateTime.Now.ToString("s"));
                sb.AppendLine("Mod " + Plugin.Version + "  game " + NetSession.GameHash);
                var s = NetSession.Instance;
                sb.AppendLine("Session: " + (s != null ? s.Status : "none") + (NetSession.Active ? (NetSession.IsHost ? " (host)" : " (guest)") : ""));
                if (s != null && s.Node != null)
                    foreach (var c in s.Node.Connections)
                        sb.AppendLine($"  peer {c.Slot}: ping {c.RttMs:F0} ms, jitter {c.RttVarMs:F0} ms, loss {c.LossPercent:F1}%, resends {c.Resends}");
                var ck = Replication.Checksum.Instance;
                if (ck != null) sb.AppendLine($"Sync checks {ck.Checks}, desyncs repaired {ck.Mismatches}, last: {ck.LastMismatch}");
                sb.AppendLine();
                Append(sb, "Settings", Path.Combine(BepInEx.Paths.ConfigPath, "gungeononline.cfg"), 200);
                Append(sb, "Desync log", Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "GungeonOnline"), "desync.log"), 300);
                Append(sb, "Metrics (last 10 minutes)", Metrics.FilePath, 120);
                Append(sb, "Mod log (last lines)", Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log"), 1500);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string file = Path.Combine(desktop, "GungeonOnline-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(file, sb.ToString());
                Toasts.Show("Report saved to your Desktop");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Report: " + e.Message);
                Toasts.Show("Could not save the report", 4f, true);
            }
        }

        static void Append(StringBuilder sb, string title, string path, int lastLines)
        {
            sb.AppendLine("==== " + title);
            try
            {
                if (!File.Exists(path)) { sb.AppendLine("(none)"); return; }
                string[] lines;
                // The log is open for writing by BepInEx; read it shared.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs))
                    lines = r.ReadToEnd().Split('\n');
                int from = Math.Max(0, lines.Length - lastLines);
                for (int i = from; i < lines.Length; i++) sb.AppendLine(lines[i].TrimEnd('\r'));
            }
            catch (Exception e) { sb.AppendLine("(could not read: " + e.Message + ")"); }
            sb.AppendLine();
        }
    }
}
