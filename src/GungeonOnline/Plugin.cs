using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace GungeonOnline
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gungeononline";
        public const string Name = "Enter The Gungeon Online Co-Op Mod";
        public const string Version = "1.0.0";

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
            Camera.IndependentCamera.Install(harmony);
            Game.RunSync.Install(harmony);
            Game.PauseScope.Install(harmony);
            Game.PresentationGate.Install(harmony);
            Game.SecondFightLock.Install(harmony);
            Replication.WorldSync.Install(harmony);
            Replication.ProjectileSync.Install(harmony);
            Replication.VitalsSync.Install(harmony);
            Replication.PickupSync.Install(harmony);
            Replication.ShopSync.Install(harmony);
            Replication.PropSync.Install(harmony);
            Session.PauseMenuEntry.Install(harmony);
            Game.BreachDoorGate.Install(harmony);
            Game.RemoteAim.Install(harmony);
            Replication.StatSync.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.DoGameOver)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(OnGameOver)));
            Dev.QuickStart.Prepare();
            Log.LogInfo($"{Name} {Version} loaded. game={Session.NetSession.GameHash} dev={Dev.DevEnv.Summary()}");
            gameObject.AddComponent<Session.NetSession>();
            gameObject.AddComponent<Session.Toasts>();
            gameObject.AddComponent<Session.Metrics>();
            gameObject.AddComponent<Game.PartnerUI>();
            gameObject.AddComponent<Session.OnlineMenu>();
            gameObject.AddComponent<Session.RunBackup>();
            gameObject.AddComponent<Game.AwayGuard>();
            gameObject.AddComponent<Session.Lobby>();
            gameObject.AddComponent<Replication.InputSync>();
            gameObject.AddComponent<Replication.PlayerSync>();
            gameObject.AddComponent<Game.RunSync>();
            gameObject.AddComponent<Replication.WorldSync>();
            gameObject.AddComponent<Replication.ProjectileSync>();
            gameObject.AddComponent<Replication.VitalsSync>();
            gameObject.AddComponent<Replication.PickupSync>();
            gameObject.AddComponent<Replication.InventorySync>();
            gameObject.AddComponent<Replication.ShopSync>();
            gameObject.AddComponent<Replication.Checksum>();
            gameObject.AddComponent<Replication.PropSync>();
            gameObject.AddComponent<Dev.DevRunner>();
            StartCoroutine(Dev.QuickStart.Run());
            Dev.AutoSession.Begin();
            StartCoroutine(Welcome());
        }

        /// <summary>Run over (everyone dead): the saved run is finished and is not offered for resuming.</summary>
        static void OnGameOver()
        {
            if (Session.NetSession.Active && Session.NetSession.IsHost) Session.RunBackup.MarkEnded();
        }

        System.Collections.IEnumerator Welcome()
        {
            if (Dev.DevEnv.IsHarness || Session.NetConfig.WelcomeShown.Value) yield break;
            yield return new UnityEngine.WaitForSeconds(6f);
            Session.Toasts.Show("Enter The Gungeon Online Co-Op Mod is ready. Pause and pick Play Online, or hold Back + Start.", 9f);
            Session.NetConfig.WelcomeShown.Value = true;
        }
    }
}
