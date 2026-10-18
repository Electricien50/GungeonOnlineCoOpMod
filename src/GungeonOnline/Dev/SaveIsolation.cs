using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Keeps automated test instances away from real saves:
    /// redirects SaveManager.SavePath and swaps Brave.PlayerPrefs (Windows registry) for an in-memory store.
    /// Does nothing unless GO_SAVE_DIR is set.
    /// </summary>
    internal static class SaveIsolation
    {
        static readonly Dictionary<string, object> prefs = new Dictionary<string, object>();

        public static void Apply(Harmony h)
        {
            if (!DevEnv.IsHarness) return;
            Directory.CreateDirectory(DevEnv.SaveDir);
            if (Traverse.Create(typeof(SaveManager)).Field("s_hasBeenInitialized").GetValue<bool>())
                Plugin.Log.LogWarning("SaveManager initialized before redirect; a real save may have been read.");
            SaveManager.SavePath = DevEnv.SaveDir;
            h.Patch(AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Init)),
                prefix: new HarmonyMethod(typeof(SaveIsolation), nameof(InitPrefix)));

            var pp = typeof(Brave.PlayerPrefs);
            void P(string name, string prefix, params Type[] args) =>
                h.Patch(AccessTools.Method(pp, name, args), prefix: new HarmonyMethod(typeof(SaveIsolation), prefix));
            P("GetInt", nameof(GetInt2), typeof(string), typeof(int));
            P("GetInt", nameof(GetInt1), typeof(string));
            P("GetFloat", nameof(GetFloat2), typeof(string), typeof(float));
            P("GetFloat", nameof(GetFloat1), typeof(string));
            P("GetString", nameof(GetString2), typeof(string), typeof(string));
            P("GetString", nameof(GetString1), typeof(string));
            P("SetInt", nameof(SetI), typeof(string), typeof(int));
            P("SetFloat", nameof(SetF), typeof(string), typeof(float));
            P("SetString", nameof(SetS), typeof(string), typeof(string));
            P("HasKey", nameof(HasKey), typeof(string));
            P("DeleteKey", nameof(DeleteKey), typeof(string));
            P("DeleteAll", nameof(DeleteAll));
            P("Save", nameof(Skip));
            Plugin.Log.LogInfo("Save isolation on: " + DevEnv.SaveDir);
        }

        static void InitPrefix() => SaveManager.SavePath = DevEnv.SaveDir;

        static T Read<T>(string k, T d) => prefs.TryGetValue(k, out var v) && v is T t ? t : d;
        static bool GetInt2(string key, int defaultValue, ref int __result) { __result = Read(key, defaultValue); return false; }
        static bool GetInt1(string key, ref int __result) { __result = Read(key, 0); return false; }
        static bool GetFloat2(string key, float defaultValue, ref float __result) { __result = Read(key, defaultValue); return false; }
        static bool GetFloat1(string key, ref float __result) { __result = Read(key, 0f); return false; }
        static bool GetString2(string key, string defaultValue, ref string __result) { __result = Read(key, defaultValue); return false; }
        static bool GetString1(string key, ref string __result) { __result = Read(key, ""); return false; }
        static bool SetI(string key, int value) { prefs[key] = value; return false; }
        static bool SetF(string key, float value) { prefs[key] = value; return false; }
        static bool SetS(string key, string value) { prefs[key] = value; return false; }
        static bool HasKey(string key, ref bool __result) { __result = prefs.ContainsKey(key); return false; }
        static bool DeleteKey(string key) { prefs.Remove(key); return false; }
        static bool DeleteAll() { prefs.Clear(); return false; }
        static bool Skip() => false;
    }
}
