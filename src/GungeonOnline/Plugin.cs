using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace GungeonOnline
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gungeononline";
        public const string Name = "Gungeon Online";
        public const string Version = "0.0.1";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        Harmony harmony;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            Dev.DevEnv.Load();
            harmony = new Harmony(Guid);
            Dev.SaveIsolation.Apply(harmony);
            Log.LogInfo($"{Name} {Version} loaded. dev={Dev.DevEnv.Summary()}");
            gameObject.AddComponent<Dev.DevRunner>();
        }
    }
}
