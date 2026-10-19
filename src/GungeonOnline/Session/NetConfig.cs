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
        public static ConfigEntry<int> LocatorStyle, NameTagStyle;
        public static ConfigEntry<bool> FriendCrosshair;
        public static ConfigEntry<int> LookFrame, LookSelect, LookTitle, LookToast;
        public static ConfigEntry<int> ConnWarning, OverlaySize, CoopStyle, NameColor, NameOpacity;
        public static ConfigEntry<int> SmoothingMs;
        public static ConfigEntry<float> CorrectionSpeed;
        public static ConfigEntry<int> WorldSnapshotHz;
        public static ConfigEntry<string> PlayerId;
        public static ConfigEntry<string> LastJoin;
        public static ConfigEntry<bool> WelcomeShown;
        public static ConfigEntry<bool> CheckUpdates;

        public static void Bind(ConfigFile c)
        {
            PlayerName = c.Bind("Session", "PlayerName", "Gungeoneer", "Name shown to other players.");
            var gn = System.Environment.GetEnvironmentVariable("GO_NAME"); // harness: made-up handles for screenshots
            if (!string.IsNullOrEmpty(gn)) { var ns = gn.Split(','); PlayerName.Value = System.Environment.GetEnvironmentVariable("GO_AUTO") == "host" || ns.Length < 2 ? ns[0] : ns[1]; }
            Port = c.Bind("Session", "Port", 47624, "UDP port used when hosting. Forward it on your router if automatic setup (UPnP) fails.");
            MaxPlayers = c.Bind("Session", "MaxPlayers", 2, new ConfigDescription("Players per session including the host. The game supports 2 today.", new AcceptableValueRange<int>(2, 4)));
            UseUpnp = c.Bind("Session", "UseUpnp", true, "Ask the router to forward the port automatically when hosting.");
            StunServer = c.Bind("Session", "StunServer", "stun.l.google.com:19302", "Server used to learn your public address for invite codes.");
            OverlayKey = c.Bind("Interface", "OverlayKey", KeyCode.F8, "Opens the online menu.");
            PauseFreezesEveryone = c.Bind("Gameplay", "PauseFreezesEveryone", false, "Host only: pausing stops the game for all players.");
            SecondFightLock = c.Bind("Gameplay", "SecondFightLock", true, "While one room is in combat, other uncleared rooms stay shut.");
            LocatorStyle = c.Bind("Interface", "LocatorStyle", 5, new ConfigDescription("When your friend is off screen, a marker at the screen edge points to them. 5 pickup tag (default), 1 speech bubble, 0 off.", new AcceptableValueRange<int>(0, 5)));
            CoopStyle = c.Bind("Gameplay", "CoopStyle", 0, new ConfigDescription("Host only. 0 Classic: start a fight and your partner is pulled in, like local co-op. 1 Explore: one fight at a time, nobody pulled.", new AcceptableValueRange<int>(0, 1)));
            var cs = System.Environment.GetEnvironmentVariable("GO_COOP_STYLE"); // harness
            if (!string.IsNullOrEmpty(cs)) { int v; if (int.TryParse(cs, out v)) CoopStyle.Value = v; }
            NameColor = c.Bind("Interface", "NameColor", 0, new ConfigDescription("Name tag color: 0 gold, 1 white, 2 sky, 3 rose, 4 mint.", new AcceptableValueRange<int>(0, 4)));
            NameOpacity = c.Bind("Interface", "NameOpacity", 0, new ConfigDescription("Name tag opacity: 0 100%, 1 75%, 2 50%.", new AcceptableValueRange<int>(0, 2)));
            FriendCrosshair = c.Bind("Interface", "FriendCrosshair", false, "Show the game's crosshair where your friend is aiming.");
            ConnWarning = c.Bind("Interface", "ConnectionWarning", 1, new ConfigDescription("Note when the connection is not great: 0 off, 1 top, 2 top faint, 3 corner, 4 corner faint.", new AcceptableValueRange<int>(0, 4)));
            OverlaySize = c.Bind("Interface", "OverlaySize", 1, new ConfigDescription("Size of the connection note, friend locator and name tags: 0 small, 1 normal, 2 large.", new AcceptableValueRange<int>(0, 2)));
            var look = new AcceptableValueRange<int>(0, 2);
            LookFrame = c.Bind("Look", "MenuFrame", 0, new ConfigDescription("Online menu frame: 0 HUD box, 1 pause menu frame, 2 death screen frame.", look));
            LookSelect = c.Bind("Look", "MenuSelection", 0, new ConfigDescription("Selected row: 0 gold with arrows, 1 gold with crosshair, 2 raised button.", look));
            LookTitle = c.Bind("Look", "MenuTitle", 0, new ConfigDescription("Menu title: 0 Alagard gold, 1 Daikon white, 2 Alagard white.", look));
            LookToast = c.Bind("Look", "Toasts", 0, new ConfigDescription("Messages: 0 pickup box, 1 speech bubble, 2 title shadow.", look));
            // Harness: GO_LOOK="frame,select,title,toast" picks a look for screenshot comparisons.
            var lk = System.Environment.GetEnvironmentVariable("GO_LOOK");
            if (!string.IsNullOrEmpty(lk))
            {
                var parts = lk.Split(',');
                int v;
                if (parts.Length > 0 && int.TryParse(parts[0], out v)) LookFrame.Value = v;
                if (parts.Length > 1 && int.TryParse(parts[1], out v)) LookSelect.Value = v;
                if (parts.Length > 2 && int.TryParse(parts[2], out v)) LookTitle.Value = v;
                if (parts.Length > 3 && int.TryParse(parts[3], out v)) LookToast.Value = v;
            }
            NameTagStyle = c.Bind("Interface", "NameTagStyle", 9, new ConfigDescription("Your friend's username: 0 off, 1 above, 6 below feet, 9 above (faint).", new AcceptableValueRange<int>(0, 10)));
            SmoothingMs = c.Bind("Network", "OtherPlayerSmoothingMs", -1, new ConfigDescription(
                "Buffer for other players' input, in ms. -1 = Auto (sized from measured jitter, recommended). 0 = lowest latency, may stutter on a jittery link. Your own character is never delayed.",
                new AcceptableValueRange<int>(-1, 100)));
            CorrectionSpeed = c.Bind("Network", "CorrectionSpeed", 0.25f, new ConfigDescription(
                "How fast small position differences from the host are blended out per frame. Lower = smoother, higher = tighter.",
                new AcceptableValueRange<float>(0.05f, 1f)));
            WorldSnapshotHz = c.Bind("Network", "WorldSnapshotHz", 120, new ConfigDescription(
                "Host only: how often enemy positions are sent. Higher = fresher, more upload. Between updates the client extrapolates.",
                new AcceptableValueRange<int>(30, 240)));
            PlayerId = c.Bind("Internal", "PlayerId", "", "Do not edit. Lets the host give you your own spot back after a disconnect.");
            if (string.IsNullOrEmpty(PlayerId.Value)) PlayerId.Value = System.Guid.NewGuid().ToString("N");
            LastJoin = c.Bind("Internal", "LastJoin", "", "Do not edit. The last game you joined, for Rejoin.");
            WelcomeShown = c.Bind("Internal", "WelcomeShown", false, "Do not edit.");
            CheckUpdates = c.Bind("General", "CheckForUpdates", true, "Once per launch, ask GitHub if a newer version is out and say so.");
        }
    }
}
