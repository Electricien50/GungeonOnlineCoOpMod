using System;
using System.Globalization;

namespace GungeonOnline.Dev
{
    /// <summary>Environment switches used by the automated test harness. All optional; players never set these.</summary>
    internal static class DevEnv
    {
        /// <summary>GO_SAVE_DIR: save folder override. Also replaces PlayerPrefs (registry) with an in-memory store.</summary>
        public static string SaveDir;
        /// <summary>GO_QUIT_AFTER: seconds after boot to quit the game.</summary>
        public static float QuitAfter = -1f;
        /// <summary>GO_STATUS: file the harness polls for a one-line heartbeat.</summary>
        public static string StatusFile;
        /// <summary>GO_SCRIPT: scripted input scenario.</summary>
        public static string Script;
        /// <summary>GO_AUTO: "host" or "join:CODE".</summary>
        public static string Auto;

        /// <summary>GO_PORT: UDP port override so several instances can host or join on one machine.</summary>
        public static int? PortOverride;
        /// <summary>GO_FPS: frame rate cap for the instance (players run uncapped; tests pick 60, 200, 240...).</summary>
        public static int Fps = 60;

        public static bool IsHarness => !string.IsNullOrEmpty(SaveDir);

        public static void Load()
        {
            SaveDir = Get("GO_SAVE_DIR");
            StatusFile = Get("GO_STATUS");
            Script = Get("GO_SCRIPT");
            Auto = Get("GO_AUTO");
            if (int.TryParse(Get("GO_PORT") ?? "", out var port)) PortOverride = port;
            if (int.TryParse(Get("GO_FPS") ?? "", out var fps) && fps > 0) Fps = fps;
            var q = Get("GO_QUIT_AFTER");
            if (q != null && float.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) QuitAfter = f;
        }

        static string Get(string k)
        {
            var v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        public static string Summary() =>
            IsHarness ? $"harness save={SaveDir} quit={QuitAfter} auto={Auto ?? "-"} script={Script ?? "-"}" : "off";
    }
}
