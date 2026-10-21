using System.Collections.Generic;
using GungeonOnline.Net;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Host side of "a run is never lost". When a friend drops during a run, their character stays (invulnerable)
    /// and the world freezes with a banner until they are back. The host can choose to continue alone; the friend
    /// can rejoin any time and gets their own character, items and spot back.
    /// </summary>
    internal sealed class AwayGuard : MonoBehaviour
    {
        public static AwayGuard Instance { get; private set; }
        readonly Dictionary<int, string> away = new Dictionary<int, string>(); // slot -> name
        public bool ContinueAlone;
        bool frozen;

        public bool AnyoneAway => away.Count > 0;

        static bool InRun => GameManager.HasInstance && GameManager.Instance.Dungeon != null && !SceneObjects.InBreach;

        void Awake() { Instance = this; }

        void Start()
        {
            var s = NetSession.Instance;
            s.PeerLeft += (c, why) =>
            {
                if (!NetSession.IsHost || !InRun) return;
                var tagName = c.Tag as string;
                away[c.Slot] = string.IsNullOrEmpty(tagName) || tagName == "Gungeoneer" ? "a player" : tagName;
                var p = PlayerSlots.ForSlot(c.Slot);
                if (p) p.healthHaver.IsVulnerable = false;
                RunBackup.SaveNow("friend dropped");
                Plugin.Log.LogInfo($"AwayGuard: slot {c.Slot} away, run waiting");
            };
            s.PeerJoined += c =>
            {
                if (!NetSession.IsHost) return;
                if (away.Remove(c.Slot))
                {
                    var p = PlayerSlots.ForSlot(c.Slot);
                    if (p) p.healthHaver.IsVulnerable = true;
                }
                // Anyone joining while a run is going (returning or not) is brought onto this floor.
                if (InRun) RunSync.Instance?.SendRejoin(c);
                // Mid floor change (no floor right now): bring them in the moment ours is ready, not 40 s later.
                else if (RunSync.CurrentIndex >= 0) RunSync.Instance?.RejoinWhenReady(c);
            };
            s.SessionEnded += () => { away.Clear(); ContinueAlone = false; Unfreeze(); };
        }

        void Update()
        {
            if (!NetSession.Active || !NetSession.IsHost || away.Count == 0 || !InRun)
            {
                if (frozen) Unfreeze();
                if (Toasts.Banner != null && Toasts.Banner.StartsWith("Waiting for")) Toasts.Banner = null;
                if (away.Count > 0 && !InRun) away.Clear();
                return;
            }
            string names = Session.GungeonUI.NameList(new List<string>(away.Values));
            if (ContinueAlone)
            {
                if (frozen) Unfreeze();
                Toasts.Banner = null;
                return;
            }
            Toasts.Banner = "Waiting for " + names + " to reconnect. The run is paused and safe.";
            if (!frozen)
            {
                BraveTime.SetTimeScaleMultiplier(0f, gameObject);
                frozen = true;
            }
        }

        void Unfreeze()
        {
            if (!frozen) return;
            BraveTime.ClearMultiplier(gameObject);
            frozen = false;
        }
    }
}
