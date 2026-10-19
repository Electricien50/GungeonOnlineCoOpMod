using System.IO;
using System.Text;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>Harness only (GO_UI_DUMP=1): writes the game's UI fonts, atlases and sprite names to the run folder.</summary>
    internal static class UiDump
    {
        static bool done;

        public static void Tick()
        {
            if (done || System.Environment.GetEnvironmentVariable("GO_UI_DUMP") != "1") return;
            if (!GameUIRoot.HasInstance || Time.realtimeSinceStartup < 25f) return;
            done = true;
            var dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
            var sb = new StringBuilder();
            foreach (var f in Resources.FindObjectsOfTypeAll<dfFont>())
            {
                if (f == null) continue;
                var tex = f.Atlas != null ? f.Atlas.Texture : null;
                sb.AppendLine($"FONT {f.name} size={f.FontSize} line={f.LineHeight} glyphs={f.Count} atlas={(f.Atlas ? f.Atlas.name : "-")} sprite={f.Sprite} tex={(tex ? tex.width + "x" + tex.height : "-")}");
            }
            foreach (var l in Object.FindObjectsOfType<dfLabel>())
                if (l && l.Font) sb.AppendLine($"LABEL {Path(l.transform)} font={l.Font.name} scale={l.TextScale} color={l.Color} text={(l.Text ?? "").Replace("\n", " ").Substring(0, Mathf.Min(30, (l.Text ?? "").Length))}");
            foreach (var s in Object.FindObjectsOfType<dfSlicedSprite>())
                if (s && s.Atlas) sb.AppendLine($"SLICED {Path(s.transform)} atlas={s.Atlas.name} sprite={s.SpriteName} size={s.Size}");
            foreach (var s in Object.FindObjectsOfType<dfSprite>())
                if (s && s.Atlas && !(s is dfSlicedSprite)) sb.AppendLine($"SPRITE {Path(s.transform)} atlas={s.Atlas.name} sprite={s.SpriteName} size={s.Size}");
            foreach (var p in Object.FindObjectsOfType<dfPanel>())
                if (p && p.Atlas && !string.IsNullOrEmpty(p.BackgroundSprite)) sb.AppendLine($"PANEL {Path(p.transform)} atlas={p.Atlas.name} sprite={p.BackgroundSprite} size={p.Size}");
            var seen = new System.Collections.Generic.HashSet<dfAtlas>();
            foreach (var a in Resources.FindObjectsOfTypeAll<dfAtlas>())
            {
                if (a == null || !seen.Add(a)) continue;
                sb.AppendLine($"ATLAS {a.name} tex={(a.Texture ? a.Texture.width + "x" + a.Texture.height : "-")} items={a.Items.Count}");
                foreach (var it in a.Items) sb.AppendLine($"  ITEM {a.name}/{it.name} px={it.sizeInPixels} border={it.border}");
                if (a.Texture) Save(a.Texture, System.IO.Path.Combine(dir, "atlas-" + a.name + ".png"));
                foreach (var it in a.Items)
                    sb.AppendLine($"  REGION {a.name}/{it.name} {it.region.x:F6} {it.region.y:F6} {it.region.width:F6} {it.region.height:F6} rot={it.rotated}");
            }
            File.WriteAllText(System.IO.Path.Combine(dir, "ui-dump.txt"), sb.ToString());
            Plugin.Log.LogInfo("Harness: UI dump written");
        }

        static string Path(Transform t)
        {
            string s = t.name;
            for (int i = 0; i < 3 && t.parent != null; i++) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }

        static void Save(Texture tex, string file)
        {
            try
            {
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var t2 = new Texture2D(tex.width, tex.height, TextureFormat.ARGB32, false);
                t2.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                t2.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                File.WriteAllBytes(file, t2.EncodeToPNG());
            }
            catch (System.Exception e) { Plugin.Log.LogInfo("UiDump: " + e.Message); }
        }
    }
}
