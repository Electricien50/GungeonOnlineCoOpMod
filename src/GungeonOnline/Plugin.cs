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
            Session.NetConfig.Bind(Config);
            Dev.DevEnv.Load();
            harmony = new Harmony(Guid);
            Dev.SaveIsolation.Apply(harmony);
            Game.BackgroundRun.Install(harmony);
            Input.InputPump.Install(harmony);
            Dev.QuickStart.Prepare();
            Log.LogInfo($"{Name} {Version} loaded. game={Session.NetSession.GameHash} dev={Dev.DevEnv.Summary()}");
            gameObject.AddComponent<Session.NetSession>();
            gameObject.AddComponent<Session.Overlay>();
            gameObject.AddComponent<Dev.DevRunner>();
            StartCoroutine(Dev.QuickStart.Run());
            Dev.AutoSession.Begin();
        }
    }
}
