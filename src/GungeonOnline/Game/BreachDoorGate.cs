using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// The Gungeon entrance in the Breach. Vanilla lets "player 1" start the run by walking in, and online each
    /// machine's own player is its player 1. So online only the host's walk-in starts the run (the friend's game
    /// follows), and only once everyone has picked a character.
    /// </summary>
    internal static class BreachDoorGate
    {
        static float lastToast;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(FoyerGungeonDoor), "OnTriggered"),
                prefix: new HarmonyMethod(typeof(BreachDoorGate), nameof(Prefix)));
        }

        static void Say(string text)
        {
            if (Time.realtimeSinceStartup - lastToast < 3f) return;
            lastToast = Time.realtimeSinceStartup;
            Toasts.Show(text, 3.5f);
        }

        static bool Prefix(FoyerGungeonDoor __instance, SpeculativeRigidbody specRigidbody)
        {
            if (!NetSession.Active || __instance.ReturnToFoyerFromTutorial || __instance.LoadsCharacterSelect) return true;
            var p = specRigidbody != null ? specRigidbody.GetComponent<PlayerController>() : null;
            if (p == null || p != PlayerSlots.Local) return true;
            if (!NetSession.IsHost)
            {
                Say("The host starts the run when they walk in");
                return false;
            }
            var lobby = Object.FindObjectOfType<Lobby>();
            if (NetSession.Instance.Node.Connections.Count > 0)
                foreach (var c in NetSession.Instance.Node.Connections)
                    if (lobby == null || !lobby.Roster.ContainsKey(c.Slot) || string.IsNullOrEmpty(lobby.Roster[c.Slot].Prefab) || PlayerSlots.ForSlot(c.Slot) == null)
                    {
                        Say("Waiting for " + Session.GungeonUI.WhoName(c) + " to pick a character");
                        return false;
                    }
            return true;
        }
    }
}
