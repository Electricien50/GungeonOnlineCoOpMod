using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// The full map is one screen in local co-op, so the game opens it when any player presses Map, keeps it open
    /// while any player holds it, lets every player pan, zoom and pick on it, and a teleport picked there moves every
    /// player. Online each machine has its own screen: the map here belongs to this machine's player alone, and its
    /// teleports move only that player (each player's own game decides where they are, see PlayerSync).
    /// </summary>
    internal static class LocalMap
    {
        static PlayerController reading; // the player whose input the game is handling right now
        static int inMap;                // inside the map screen's own code: it sees only our player
        static PlayerController[] onlyLocal = new PlayerController[1];

        /// <summary>This machine's full map is open.</summary>
        public static bool Open => NetSession.Active && Minimap.HasInstance && Minimap.Instance.IsFullscreen;

        public static void Install(Harmony h)
        {
            var t = typeof(LocalMap);
            h.Patch(AccessTools.Method(typeof(PlayerController), "HandlePlayerInput"),
                prefix: new HarmonyMethod(t, nameof(InputPrefix)),
                finalizer: new HarmonyMethod(t, nameof(InputFinalizer)));
            h.Patch(AccessTools.Method(typeof(Minimap), nameof(Minimap.ToggleMinimap)),
                prefix: new HarmonyMethod(t, nameof(TogglePrefix)));
            h.Patch(AccessTools.Method(typeof(MinimapUIController), "Update"),
                prefix: new HarmonyMethod(t, nameof(EnterMap)),
                postfix: new HarmonyMethod(t, nameof(UiPostfix)),
                finalizer: new HarmonyMethod(t, nameof(LeaveMap)));
            // Opening and closing the map stops and frees every player's hands: only ours here.
            h.Patch(AccessTools.Method(typeof(MinimapUIController), nameof(MinimapUIController.ToggleState)),
                prefix: new HarmonyMethod(t, nameof(EnterMap)),
                finalizer: new HarmonyMethod(t, nameof(LeaveMap)));
            h.Patch(AccessTools.PropertyGetter(typeof(GameManager), nameof(GameManager.AllPlayers)),
                postfix: new HarmonyMethod(t, nameof(AllPlayersPostfix)));
            // Picking a friend's marker on the map (click, or Interact with the map centered on it) joins them.
            h.Patch(AccessTools.Method(typeof(MinimapUIController), "AttemptTeleport"),
                prefix: new HarmonyMethod(t, nameof(TeleportPrefix)));
        }

        // ---------------- Join a friend from the map ----------------

        static tk2dBaseSprite outlined;

        /// <summary>Where on the floor the map shows at a screen point (the game's own conversion, CheckIconsNearCursor).</summary>
        static Vector2 MapToWorld(Minimap map, Vector2 screen)
        {
            Vector2 vp = BraveUtility.GetMinimapViewportPosition(screen);
            vp.x = (vp.x - 0.5f) * (BraveCameraUtility.ASPECT / 1.7777778f) + 0.5f;
            return (map.cameraRef.ViewportPointToRay(vp).origin.XY() - map.transform.position.XY()) * 8f;
        }

        /// <summary>The friend whose marker is under the mouse (or the middle of the map, on a controller).</summary>
        static PlayerController FriendUnderPointer(Minimap map, PlayerController me)
        {
            if (map == null || map.cameraRef == null || !me) return null;
            var bi = BraveInput.GetInstanceForPlayer(me.PlayerIDX);
            Vector2 screen = bi != null && bi.IsKeyboardAndMouse() ? (Vector2)UnityEngine.Input.mousePosition : new Vector2(Screen.width / 2f, Screen.height / 2f);
            Vector2 at = MapToWorld(map, screen);
            // About the size of a marker on screen, whatever the zoom.
            float reach = Mathf.Max(1.5f, (MapToWorld(map, screen + new Vector2(Screen.height * 0.035f, 0f)) - at).magnitude);
            PlayerController best = null;
            float bestDist = reach;
            foreach (var kv in PlayerSlots.Remotes) // not GameManager.AllPlayers: the map screen sees only us
            {
                var p = kv.Value;
                if (!p || p == me || p.IsGhost || p.healthHaver == null || p.healthHaver.IsDead) continue;
                float d = (p.CenterPosition - at).magnitude;
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        /// <summary>Why we cannot join that friend now, or null. Never into or out of a fight.</summary>
        static string CannotJoin(PlayerController me, PlayerController friend)
        {
            var mine = me.CurrentRoom;
            var theirs = friend.CurrentRoom;
            if (Minimap.Instance.PreventAllTeleports || GameUIRoot.Instance.DisplayingConversationBar) return "You can't teleport right now";
            if (me.IsGhost || me.healthHaver.IsDead || me.IsFalling || me.IsInMinecart || friend.IsInMinecart || friend.IsFalling) return "You can't teleport right now";
            if (mine == null || theirs == null) return "You can't teleport right now";
            if (mine.IsSealed || mine.CompletelyPreventLeaving) return "You can't leave during a fight";
            if (theirs.IsSealed) return "Your friend is in a fight";
            if (theirs.area != null && theirs.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS && theirs.HasActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear))
                return "Your friend is in a fight";
            return null;
        }

        static bool TeleportPrefix(ref bool __result)
        {
            if (!NetSession.Active) return true;
            var map = Minimap.Instance;
            var me = PlayerSlots.Local;
            var friend = FriendUnderPointer(map, me);
            if (!friend) return true; // a teleporter: the game's own teleport (ours only, see AllPlayersPostfix)
            __result = false;
            var why = CannotJoin(me, friend);
            if (why != null) { Toasts.Show(why, 2.5f, true); return false; }
            var spot = Camera.IndependentCamera.SafeSpotNear(me, friend);
            if (spot == null) { Toasts.Show("No room to land next to your friend", 2.5f, true); return false; }
            map.ToggleMinimap(false);
            LootEngine.DoDefaultPurplePoof(me.CenterPosition, ignoreTimeScale: true);
            // Our own move: our game decides where we are, and the other screens follow (PlayerSync).
            me.WarpToPoint(spot.Value, true);
            me.healthHaver.TriggerInvulnerabilityPeriod();
            AkSoundEngine.PostEvent("Play_OBJ_teleport_arrive_01", me.gameObject);
            Plugin.Log.LogInfo($"MAPJOIN slot {PlayerSlots.SlotOf(me)} joined slot {PlayerSlots.SlotOf(friend)} in {friend.CurrentRoom.GetRoomName()} at {spot.Value}");
            __result = true;
            return false;
        }

        /// <summary>Outline the friend's marker while it is under the pointer, like the game does for teleporters.</summary>
        static void HighlightFriend(Minimap map, PlayerController me)
        {
            tk2dBaseSprite want = null;
            var friend = map.IsFullscreen ? FriendUnderPointer(map, me) : null;
            if (friend)
            {
                // The marker drawn for that friend: the one nearest to where the map puts them.
                Vector3 expect = map.transform.position + new Vector3(friend.CenterPosition.x * 0.125f, friend.CenterPosition.y * 0.125f, 0f);
                float best = float.MaxValue;
                foreach (var s in map.GetComponentsInChildren<tk2dBaseSprite>())
                {
                    if (!s || !s.renderer || !s.renderer.enabled || s.renderer.sortingLayerName != "Foreground") continue;
                    float d = ((Vector2)(s.transform.position - expect)).sqrMagnitude;
                    if (d < best) { best = d; want = s; }
                }
                if (best > 4f) want = null;
            }
            if (want == outlined) return;
            if (outlined) SpriteOutlineManager.RemoveOutlineFromSprite(outlined);
            outlined = want;
            if (outlined) SpriteOutlineManager.AddOutlineToSprite(outlined, Color.white);
        }

        static void InputPrefix(PlayerController __instance, out PlayerController __state)
        {
            __state = reading;
            reading = __instance;
        }

        static System.Exception InputFinalizer(System.Exception __exception, PlayerController __state)
        {
            reading = __state;
            return __exception;
        }

        static void EnterMap() => inMap++;

        static System.Exception LeaveMap(System.Exception __exception)
        {
            if (inMap > 0) inMap--;
            return __exception;
        }

        /// <summary>The map screen's loops over "all players" (input, zoom, teleport) see only this machine's player.</summary>
        static void AllPlayersPostfix(ref PlayerController[] __result)
        {
            if (inMap == 0 || !NetSession.Active) return;
            var me = PlayerSlots.Local;
            if (!me) return;
            onlyLocal[0] = me;
            __result = onlyLocal;
        }

        /// <summary>Another machine's player pressing Map opens it on their screen, not here.</summary>
        static bool TogglePrefix(bool fullscreen) =>
            !(fullscreen && NetSession.Active && reading != null && PlayerSlots.IsRemote(reading));

        /// <summary>The game keeps the map open while the other player holds Map: close it once ours is let go.</summary>
        static void UiPostfix()
        {
            if (!NetSession.Active) return;
            var map = Minimap.Instance;
            var me = PlayerSlots.Local;
            if (map != null && me)
            {
                try { HighlightFriend(map, me); }
                catch (System.Exception e) { Plugin.Log.LogWarning("LocalMap highlight: " + e.Message); outlined = null; }
            }
            if (map == null || !map.IsFullscreen || map.HeldOpen || !me) return;
            var input = BraveInput.GetInstanceForPlayer(me.PlayerIDX);
            var actions = input != null ? input.ActiveActions : null;
            if (actions != null && !actions.MapAction.IsPressed) map.ToggleMinimap(false);
        }
    }
}
