using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Adds "PLAY ONLINE" to Gungeon's own pause menu, right under "Return to game", in the same style
    /// (a copy of the Options button) and reachable with the d-pad like the other entries.
    /// </summary>
    internal static class PauseMenuEntry
    {
        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PauseMenuController), "Start"),
                postfix: new HarmonyMethod(typeof(PauseMenuEntry), nameof(AfterStart)));
            h.Patch(AccessTools.Method(typeof(PauseMenuController), "DoExitToMainMenu"),
                prefix: new HarmonyMethod(typeof(PauseMenuEntry), nameof(BeforeExit)));
            h.Patch(AccessTools.Method(typeof(PauseMenuController), "DoQuickRestart"),
                prefix: new HarmonyMethod(typeof(PauseMenuEntry), nameof(BeforeQuickRestart)));
            h.Patch(AccessTools.Method(typeof(PauseMenuController), "DoQuitGameEntirely"),
                prefix: new HarmonyMethod(typeof(PauseMenuEntry), nameof(BeforeQuit)));
        }

        /// <summary>
        /// Online, "Exit Coop" in the Breach would delete your friend's character on your side only: it opens the
        /// leave screen instead. Quitting to the main menu mid-run saves a backup first and leaves cleanly.
        /// </summary>
        static bool BeforeExit()
        {
            if (!NetSession.Active) return true;
            if (Game.SceneObjects.InBreach)
            {
                GameManager.Instance.Unpause();
                OnlineMenu.Instance?.OpenLeave();
                return false;
            }
            RunBackup.SaveNow("quit to menu");
            if (!NetSession.IsHost) NetSession.Instance.Leave("left");
            return true;
        }

        /// <summary>
        /// Quick restart would throw away the run on one machine only. Online it is off; a new run starts from the
        /// Breach like any other, so a stray button press can never cost the run.
        /// </summary>
        static bool BeforeQuickRestart()
        {
            if (!NetSession.Active) return true;
            GameManager.Instance.Unpause();
            Toasts.Show("Quick restart is off online so a run is never lost by accident", 4f);
            return false;
        }

        static void BeforeQuit()
        {
            if (NetSession.Active) RunBackup.SaveNow("quit game");
        }

        static void AfterStart(PauseMenuController __instance)
        {
            try { Add(__instance); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Pause menu entry: " + e.Message + " (F8 and Back+Start still open the menu)"); }
        }

        static void Add(PauseMenuController menu)
        {
            var template = menu.OptionsButton;
            var top = menu.ReturnToGameButton;
            var next = menu.BestiaryButton;
            if (template == null || top == null || next == null) return;
            var parent = template.Parent;
            if (parent == null) return;

            var go = Object.Instantiate(template.gameObject);
            go.name = "GungeonOnlineButton";
            var button = go.GetComponent<dfButton>();
            parent.AddControl(button);
            button.IsLocalized = false;
            button.Text = "PLAY ONLINE";

            // Make room inside the menu frame: the first entry moves up half a row, the rest down half a row.
            float step = next.RelativePosition.y - top.RelativePosition.y;
            float half = Mathf.Round(step / 2f);
            float topY = top.RelativePosition.y;
            top.RelativePosition -= new Vector3(0f, half, 0f);
            foreach (var b in new[] { menu.BestiaryButton, menu.OptionsButton, menu.QuickRestartButton, menu.ExitToMainMenuButton, menu.QuitGameButton })
                if (b != null && b.RelativePosition.y >= next.RelativePosition.y - 0.5f) b.RelativePosition += new Vector3(0f, step - half, 0f);
            button.RelativePosition = new Vector3(next.RelativePosition.x, topY - half + step, next.RelativePosition.z);

            // D-pad links.
            var keys = go.GetComponent<UIKeyControls>();
            var topKeys = top.GetComponent<UIKeyControls>();
            var nextKeys = next.GetComponent<UIKeyControls>();
            if (keys != null) { keys.up = top; keys.down = next; }
            if (topKeys != null) topKeys.down = button;
            if (nextKeys != null) nextKeys.up = button;

            button.Click += (c, e) =>
            {
                if (GameManager.Instance.IsLoadingLevel) return;
                GameManager.Instance.Unpause();
                OnlineMenu.Instance?.Open();
            };
            Plugin.Log.LogInfo("Pause menu: Play Online added");
        }
    }
}
