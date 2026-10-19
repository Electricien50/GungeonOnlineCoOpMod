using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Draws with the game's own UI art: sprites and 9-slice frames from GameUIAtlas, and text in the game's
    /// bitmap fonts (Daikon for menus, 04b03 for small print, Alagard for titles), glyph by glyph exactly as
    /// dfGUI renders them. Everything snaps to the game's pixel grid so it sits next to the vanilla HUD
    /// without looking added on.
    /// </summary>
    internal static class GungeonUI
    {
        public enum Font { Menu, Small, Title }

        static dfAtlas ui;
        static dfFont menuFont, smallFont, titleFont;
        static float nextLookup;

        /// <summary>True once the game's UI assets are loaded (a few frames after boot).</summary>
        public static bool Ready
        {
            get
            {
                if (ui != null && menuFont != null && smallFont != null && titleFont != null) return true;
                if (Time.realtimeSinceStartup < nextLookup) return ui != null && menuFont != null && smallFont != null;
                nextLookup = Time.realtimeSinceStartup + 1f;
                foreach (var a in Resources.FindObjectsOfTypeAll<dfAtlas>()) if (a != null && a.name == "GameUIAtlas") { ui = a; break; }
                foreach (var f in Resources.FindObjectsOfTypeAll<dfFont>())
                {
                    if (f == null) continue;
                    if (f.name == "OurFont_DaikonBitmap") menuFont = f;
                    else if (f.name == "04b03_8pt") smallFont = f;
                    else if (f.name == "Alagard30") titleFont = f;
                }
                return ui != null && menuFont != null && smallFont != null;
            }
        }

        /// <summary>Overlay size setting as a text scale (small, normal, large).</summary>
        public static float OverlayScale => NetConfig.OverlaySize == null ? 1f : NetConfig.OverlaySize.Value == 0 ? 0.75f : NetConfig.OverlaySize.Value == 2 ? 1.5f : 1f;

        /// <summary>Screen pixels per game pixel (the game is 480x270 pixel art).</summary>
        public static int Px => Mathf.Max(1, Mathf.RoundToInt(Screen.height / 270f));

        public static readonly Color Gold = new Color(1f, 0.82f, 0.33f);
        public static readonly Color Cream = new Color(0.96f, 0.93f, 0.85f);
        public static readonly Color Shadow = new Color(0.04f, 0.02f, 0.05f, 0.85f);

        // ---------- Sprites ----------

        static readonly Dictionary<string, dfAtlas.ItemInfo> items = new Dictionary<string, dfAtlas.ItemInfo>();

        public static dfAtlas.ItemInfo Item(string name)
        {
            if (!Ready) return null;
            if (items.TryGetValue(name, out var it)) return it;
            it = ui[name];
            if (it != null && it.rotated) it = null; // rotated atlas entries are not used by this UI
            items[name] = it;
            return it;
        }

        public static Vector2 SpriteSize(string name)
        {
            var it = Item(name);
            return it == null ? Vector2.zero : it.sizeInPixels * Px;
        }

        public static bool Sprite(string name, Rect dest, Color tint)
        {
            var it = Item(name);
            if (it == null) return false;
            var old = GUI.color;
            GUI.color = tint * old;
            GUI.DrawTextureWithTexCoords(Snap(dest), ui.Texture, it.region);
            GUI.color = old;
            return true;
        }

        /// <summary>A sprite at its natural size (times the pixel scale), centered on a point.</summary>
        public static Rect SpriteAt(string name, Vector2 center, Color tint, float scale = 1f)
        {
            var size = SpriteSize(name) * scale;
            var r = new Rect(center.x - size.x / 2f, center.y - size.y / 2f, size.x, size.y);
            Sprite(name, r, tint);
            return r;
        }

        /// <summary>A 9-slice frame from the atlas, stretched to fill a rectangle; corners keep their pixel size.</summary>
        public static bool Frame(string name, Rect dest, Color tint) => Frame(name, dest, tint, null);

        /// <summary>9-slice with chosen border widths (in sprite pixels), for drawing a frame smaller than its art allows.</summary>
        public static bool Frame(string name, Rect dest, Color tint, RectOffset border)
        {
            var it = Item(name);
            if (it == null) return false;
            dest = Snap(dest);
            var b = border ?? it.border;
            if (b.left + b.right == 0 && b.top + b.bottom == 0) return Sprite(name, dest, tint);
            float px = Px, uw = it.region.width / it.sizeInPixels.x, vh = it.region.height / it.sizeInPixels.y;
            float[] xs = { dest.xMin, dest.xMin + b.left * px, dest.xMax - b.right * px, dest.xMax };
            float[] ys = { dest.yMin, dest.yMin + b.top * px, dest.yMax - b.bottom * px, dest.yMax };
            float[] us = { it.region.xMin, it.region.xMin + b.left * uw, it.region.xMax - b.right * uw, it.region.xMax };
            float[] vs = { it.region.yMax, it.region.yMax - b.top * vh, it.region.yMin + b.bottom * vh, it.region.yMin };
            var old = GUI.color;
            GUI.color = tint * old;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    var r = new Rect(xs[i], ys[j], xs[i + 1] - xs[i], ys[j + 1] - ys[j]);
                    if (r.width <= 0 || r.height <= 0) continue;
                    var uv = new Rect(us[i], vs[j + 1], us[i + 1] - us[i], vs[j] - vs[j + 1]);
                    GUI.DrawTextureWithTexCoords(r, ui.Texture, uv);
                }
            GUI.color = old;
            return true;
        }

        /// <summary>The 9px head the minimap uses for a character.</summary>
        public static string HeadFor(PlayerController p)
        {
            if (p == null) return null;
            switch (p.characterIdentity)
            {
                case PlayableCharacters.Pilot: return "Player_Rogue_001";
                case PlayableCharacters.Convict: return "Player_Convict_001";
                case PlayableCharacters.Robot: return "Player_Robot_001";
                case PlayableCharacters.Ninja: return "Player_Ninja_001";
                case PlayableCharacters.Cosmonaut: return "Player_Cosmonaut_001";
                case PlayableCharacters.Soldier: return "Player_Marine_001";
                case PlayableCharacters.Guide: return "Player_Guide_001";
                case PlayableCharacters.CoopCultist: return "Player_Coop_001";
                case PlayableCharacters.Bullet: return "Player_Bullet_001";
                case PlayableCharacters.Eevee: return "Player_Eevee_minimap_001";
                case PlayableCharacters.Gunslinger: return "Player_Slinger_001";
                default: return "Player_Coop_001";
            }
        }

        // ---------- Text ----------

        static dfFont FontOf(Font f) => f == Font.Small ? smallFont : f == Font.Title ? (titleFont ?? menuFont) : menuFont;

        /// <summary>Glyph pixels per game pixel, chosen so each font has the height the game gives it.</summary>
        static float GlyphScale(Font f)
        {
            if (f != Font.Title) return 1f;
            if (titleFont == null) return 1.15f; // Daikon stands in until the title font has loaded
            return 16f / Mathf.Max(1f, TallestGlyph(titleFont));
        }

        static float tallest;
        static float TallestGlyph(dfFont f)
        {
            if (tallest > 0) return tallest;
            foreach (var g in f.Glyphs) if (g != null && g.id >= 'A' && g.id <= 'Z') tallest = Mathf.Max(tallest, g.height);
            return tallest > 0 ? tallest : f.LineHeight;
        }

        /// <summary>Friendly name for a player who has not set one: their character, the way the game names them.</summary>
        public static string CharacterName(PlayerController p)
        {
            if (p == null) return "Your friend";
            switch (p.characterIdentity)
            {
                case PlayableCharacters.Pilot: return "The Pilot";
                case PlayableCharacters.Convict: return "The Convict";
                case PlayableCharacters.Robot: return "The Robot";
                case PlayableCharacters.Ninja: return "Your friend";
                case PlayableCharacters.Cosmonaut: return "The Cosmonaut";
                case PlayableCharacters.Soldier: return "The Marine";
                case PlayableCharacters.Guide: return "The Hunter";
                case PlayableCharacters.CoopCultist: return "The Cultist";
                case PlayableCharacters.Bullet: return "The Bullet";
                case PlayableCharacters.Eevee: return "The Paradox";
                case PlayableCharacters.Gunslinger: return "The Gunslinger";
                default: return "Your friend";
            }
        }

        /// <summary>A solid dark backing in the game's UI black, for panels whose frame art is see-through.</summary>
        public static void Fill(Rect r, float alpha = 0.94f)
        {
            var old = GUI.color;
            GUI.color = new Color(0.035f, 0.03f, 0.045f, alpha * old.a);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        public static Vector2 Measure(string text, Font f, float scale = 1f)
        {
            var font = FontOf(f);
            if (font == null || string.IsNullOrEmpty(text)) return Vector2.zero;
            float s = Px * GlyphScale(f) * scale, w = 0;
            char prev = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                var g = font.GetGlyph(text[i]);
                if (g == null) continue;
                if (i > 0) w += font.GetKerning(prev, text[i]) * s;
                w += g.xadvance * s;
                prev = text[i];
            }
            return new Vector2(Mathf.Round(w), Mathf.Round(font.LineHeight * s));
        }

        /// <summary>Text with the game's drop shadow (one game pixel down and right), top-left at pos.</summary>
        public static Vector2 Text(string text, Vector2 pos, Font f, Color color, float scale = 1f, bool shadow = true)
        {
            if (!Ready || string.IsNullOrEmpty(text)) return Vector2.zero;
            float s = Px * GlyphScale(f) * scale;
            if (shadow) Glyphs(text, pos + new Vector2(Mathf.Max(1f, Px * scale), Mathf.Max(1f, Px * scale)), f, s, new Color(Shadow.r, Shadow.g, Shadow.b, Shadow.a * color.a));
            return Glyphs(text, pos, f, s, color);
        }

        /// <summary>Text centered horizontally on x, top at y.</summary>
        public static Vector2 TextCentered(string text, float x, float y, Font f, Color color, float scale = 1f, bool shadow = true)
        {
            var size = Measure(text, f, scale);
            Text(text, new Vector2(x - size.x / 2f, y), f, color, scale, shadow);
            return size;
        }

        static Vector2 Glyphs(string text, Vector2 pos, Font f, float s, Color color)
        {
            var font = FontOf(f);
            var tex = font.Atlas.Texture;
            var region = font.Atlas[font.Sprite].region;
            float tw = tex.width, th = tex.height;
            var old = GUI.color;
            GUI.color = color * old;
            float x = Mathf.Round(pos.x), y = Mathf.Round(pos.y);
            char prev = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                var g = font.GetGlyph(c);
                if (g == null) continue;
                if (i > 0) x += font.GetKerning(prev, c) * s;
                if (g.width > 0 && g.height > 0)
                {
                    var dest = new Rect(x + g.xoffset * s, y + g.yoffset * s, g.width * s, g.height * s);
                    float u = region.x + g.x / tw, vTop = region.yMax - g.y / th;
                    var uv = new Rect(u, vTop - g.height / th, g.width / tw, g.height / th);
                    GUI.DrawTextureWithTexCoords(dest, tex, uv);
                }
                x += g.xadvance * s;
                prev = c;
            }
            GUI.color = old;
            return new Vector2(x - pos.x, font.LineHeight * s);
        }

        static Rect Snap(Rect r) => new Rect(Mathf.Round(r.x), Mathf.Round(r.y), Mathf.Round(r.width), Mathf.Round(r.height));

        // ---------- Motion ----------

        /// <summary>Smooth 0..1 ease for fades and pops.</summary>
        public static float Ease(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }
    }
}
