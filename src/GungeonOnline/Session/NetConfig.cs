using BepInEx.Configuration;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>All user-tunable settings, stored in BepInEx/config/gungeononline.cfg.</summary>
    internal static class NetConfig
    {
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<int> Port;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<bool> UseUpnp;
        public static ConfigEntry<string> StunServer;
        public static ConfigEntry<KeyCode> OverlayKey;
        public static ConfigEntry<bool> PauseFreezesEveryone;
        public static ConfigEntry<bool> SecondFightLock;
        public static ConfigEntry<int> SmoothingMs;
        public static ConfigEntry<float> CorrectionSpeed;
        public static ConfigEntry<int> WorldSnapshotHz;

        public static void Bind(ConfigFile c)
        {
            PlayerName = c.Bind("Session", "PlayerName", "Gungeoneer", "Name shown to other players.");
            Port = c.Bind("Session", "Port", 47624, "UDP port used when hosting. Forward it on your router if automatic setup (UPnP) fails.");
            MaxPlayers = c.Bind("Session", "MaxPlayers", 2, new ConfigDescription("Players per session including the host. The game supports 2 today.", new AcceptableValueRange<int>(2, 4)));
            UseUpnp = c.Bind("Session", "UseUpnp", true, "Ask the router to forward the port automatically when hosting.");
            StunServer = c.Bind("Session", "StunServer", "stun.l.google.com:19302", "Server used to learn your public address for invite codes.");
            OverlayKey = c.Bind("Interface", "OverlayKey", KeyCode.F8, "Opens the Gungeon Online window.");
            PauseFreezesEveryone = c.Bind("Gameplay", "PauseFreezesEveryone", false, "Host only: pausing stops the game for all players.");
            SecondFightLock = c.Bind("Gameplay", "SecondFightLock", true, "While one room is in combat, other uncleared rooms stay shut.");
            SmoothingMs = c.Bind("Network", "OtherPlayerSmoothingMs", -1, new ConfigDescription(
                "Buffer for other players' input, in ms. -1 = Auto (sized from measured jitter, recommended). 0 = lowest latency, may stutter on a jittery link. Your own character is never delayed.",
                new AcceptableValueRange<int>(-1, 100)));
            CorrectionSpeed = c.Bind("Network", "CorrectionSpeed", 0.25f, new ConfigDescription(
                "How fast small position differences from the host are blended out per frame. Lower = smoother, higher = tighter.",
                new AcceptableValueRange<float>(0.05f, 1f)));
            WorldSnapshotHz = c.Bind("Network", "WorldSnapshotHz", 120, new ConfigDescription(
                "Host only: how often enemy positions are sent. Higher = fresher, more upload. Between updates the client extrapolates.",
                new AcceptableValueRange<int>(30, 240)));
        }
    }
}
