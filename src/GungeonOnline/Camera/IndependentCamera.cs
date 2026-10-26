using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using GungeonOnline.Game;

namespace GungeonOnline.Camera
{
    /// <summary>
    /// Each machine's camera follows only its own player. Vanilla co-op averages both players and pulls
    /// a player who strays into the partner's fight; both are off during an online session.
    /// </summary>
    internal static class IndependentCamera
    {
        public static void Install(Harmony h)
        {
            // Classic co-op style: the vanilla pull into your partner's fight, decided on the host (a guest's
            // character is moved there by the host and follows). Explore and Free for all: no pull.
            h.Patch(AccessTools.Method(typeof(PlayerController), "HandleCoopSpecificTimers"),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(PullPrefix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), nameof(PlayerController.ReuniteWithOtherPlayer)),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(PullSafePrefix)),
                postfix: new HarmonyMethod(typeof(IndependentCamera), nameof(PulledPostfix)));
            // Vanilla co-op stops a player at the edge of the shared camera. With a camera per machine that
            // would pin the other player to the edge of your screen, so it is off online.
            h.Patch(AccessTools.Method(typeof(PlayerController), "CameraBoundsMovementRestrictor"),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(SkipInSession)));
            // The shared camera ignores ghosts; your own camera must keep following you as a ghost.
            h.Patch(AccessTools.Method(typeof(CameraController), nameof(CameraController.GetCoreCurrentBasePosition)),
                transpiler: new HarmonyMethod(typeof(IndependentCamera), nameof(GhostFieldTranspiler)));
            // Vanilla warps a ghost that leaves the shared camera back to its partner; online each has a camera.
            h.Patch(AccessTools.Method(typeof(PlayerController), "Update"),
                transpiler: new HarmonyMethod(typeof(IndependentCamera), nameof(GhostVisibilityTranspiler)));
        }

        static IEnumerable<CodeInstruction> GhostFieldTranspiler(IEnumerable<CodeInstruction> code)
        {
            var field = AccessTools.Field(typeof(PlayerController), nameof(PlayerController.IsGhost));
            var repl = AccessTools.Method(typeof(IndependentCamera), nameof(GhostForCamera));
            foreach (var ci in code)
            {
                if (ci.opcode == OpCodes.Ldfld && ci.operand as System.Reflection.FieldInfo == field)
                    yield return new CodeInstruction(OpCodes.Call, repl) { labels = ci.labels };
                else yield return ci;
            }
        }

        public static bool GhostForCamera(PlayerController p) => p.IsGhost && !(Session.NetSession.Active && p == PlayerSlots.Local);

        static IEnumerable<CodeInstruction> GhostVisibilityTranspiler(IEnumerable<CodeInstruction> code)
        {
            var target = AccessTools.Method(typeof(CameraController), nameof(CameraController.PointIsVisible), new[] { typeof(UnityEngine.Vector2), typeof(float) });
            var repl = AccessTools.Method(typeof(IndependentCamera), nameof(GhostPointVisible));
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand as System.Reflection.MethodInfo == target)
                    yield return new CodeInstruction(OpCodes.Call, repl) { labels = ci.labels };
                else yield return ci;
            }
        }

        public static bool GhostPointVisible(CameraController cc, UnityEngine.Vector2 point, float buffer) =>
            Session.NetSession.Active || cc.PointIsVisible(point, buffer);

        static bool SkipInSession() => !Session.NetSession.Active;

        static bool PullPrefix(PlayerController __instance)
        {
            if (!Session.NetSession.Active) return true;
            if (!Session.NetSession.IsHost || Session.NetConfig.CoopStyle.Value != 0) return false;
            if (!Game.NPlayers.Players.NPlus) return true;
            // Three or four players: the game's timer only follows one partner.
            Game.NPlayers.GatherPatches.PullForN(__instance);
            return false;
        }

        static System.Collections.IEnumerator CheckLanding(PlayerController p)
        {
            float end = UnityEngine.Time.realtimeSinceStartup + 2f;
            while (UnityEngine.Time.realtimeSinceStartup < end) yield return null;
            if (!p || GameManager.Instance.Dungeon == null) yield break;
            var d = GameManager.Instance.Dungeon.data;
            var at = p.specRigidbody ? p.specRigidbody.UnitCenter.ToIntVector2(VectorConversions.Floor) : p.transform.position.IntXY(VectorConversions.Floor);
            var c = d.CheckInBounds(at.x, at.y) ? d.cellData[at.x][at.y] : null;
            bool bad = c == null || c.type != Dungeonator.CellType.FLOOR || c.isDoorFrameCell || c.isExitCell;
            Plugin.Log.LogInfo($"PULLCHECK {(bad ? "BAD" : "ok")} cell={(c != null ? c.type.ToString() : "none")} door={(c != null && (c.isDoorFrameCell || c.isExitCell))} at={at}");
        }

        static readonly bool oldPull = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_OLD_PULL") == "1";

        /// <summary>The player a pull brings this one to: the host's own player when they are in that fight,
        /// otherwise the one the game chose.</summary>
        public static PlayerController PullAnchor(PlayerController me, PlayerController other)
        {
            var host = PlayerSlots.Local;
            if (Session.NetSession.IsHost && host && host != me && other != null && Game.NPlayers.Players.IsLiving(host) && host.CurrentRoom == other.CurrentRoom)
                return host;
            return other;
        }

        /// <summary>Vanilla puts the pulled player on the exact spot of the one who walked in, which is often the
        /// doorway: the door then seals on top of them and they end up in the wall. Land on the clear floor nearest
        /// to where the host's player stands right now, away from doors, instead.</summary>
        static bool PullSafePrefix(PlayerController __instance, PlayerController other, bool useDefaultVFX)
        {
            if (!Session.NetSession.Active || oldPull || other == null) return true;
            other = PullAnchor(__instance, other);
            var to = SafeSpotNear(__instance, other);
            Plugin.Log.LogInfo($"PULLSAFE {(to != null ? "to " + to.Value : "none found")} from {(UnityEngine.Vector2)other.transform.position} room={(other.CurrentRoom != null ? other.CurrentRoom.GetRoomName() : "none")}");
            if (to == null) return true;
            __instance.WarpToPoint(to.Value, useDefaultVFX);
            return false;
        }

        static bool Clear(Dungeonator.DungeonData d, int x, int y, Dungeonator.RoomHandler room)
        {
            if (!d.CheckInBounds(x, y)) return false;
            var c = d.cellData[x][y];
            return c != null && c.type == Dungeonator.CellType.FLOOR && (room == null || c.parentRoom == room) && !c.isExitCell && !c.isDoorFrameCell &&
                !c.doesDamage && !c.IsPlayerInaccessible && !c.containsTrap;
        }

        /// <summary>Clear floor next to <paramref name="other"/> where <paramref name="mover"/> fits.</summary>
        public static UnityEngine.Vector2? SafeSpotNear(PlayerController mover, PlayerController other) =>
            SafeSpotNear(mover, other.transform.position, other.CurrentRoom);

        public static UnityEngine.Vector2? SafeSpotNear(PlayerController mover, UnityEngine.Vector2 point, Dungeonator.RoomHandler room)
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.Dungeon == null) return null;
            var d = gm.Dungeon.data;
            var at = point.ToIntVector2(VectorConversions.Floor);
            // Floor cells can still hold a wall's collision (a wall face, a pillar) or a placed object, which left
            // pulled players stuck: test the player's own body there too. Room first, a step back from walls first.
            return Search(d, at, room, mover, true) ?? Search(d, at, room, mover, false) ?? Search(d, at, room, null, false)
                ?? Search(d, at, null, mover, false) ?? Search(d, at, null, null, false);
        }

        static bool IsWall(Dungeonator.DungeonData d, int x, int y)
        {
            if (!d.CheckInBounds(x, y)) return true;
            var c = d.cellData[x][y];
            return c == null || c.type == Dungeonator.CellType.WALL || c.IsTopWall();
        }

        /// <summary>The player's body placed at <paramref name="at"/> touches nothing solid (walls, pillars, tables).</summary>
        static bool BodyFits(PlayerController p, UnityEngine.Vector2 at)
        {
            var rb = p.specRigidbody;
            if (!rb || PhysicsEngine.Instance == null) return true;
            try
            {
                // Enemies, players, shots and loot do not trap anyone.
                return !PhysicsEngine.Instance.OverlapCast(rb, null, true, true, null, null, false, at,
                    o => o.GetComponent<GameActor>() || o.GetComponent<Projectile>() || o.GetComponent<PickupObject>() || o.GetComponent<DebrisObject>());
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("PULLSAFE body test: " + e.Message); return true; }
        }

        static UnityEngine.Vector2? Search(Dungeonator.DungeonData d, IntVector2 at, Dungeonator.RoomHandler room, PlayerController mover, bool margin)
        {
            for (int r = 0; r <= 12; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != r) continue;
                        int x = at.x + dx, y = at.y + dy;
                        bool ok = true;
                        // A 2 by 2 footprint, and nothing within two cells that is a door.
                        for (int i = -2; i <= 3 && ok; i++)
                            for (int j = -2; j <= 3 && ok; j++)
                            {
                                bool foot = i >= 0 && i <= 1 && j >= 0 && j <= 1;
                                if (foot) ok = Clear(d, x + i, y + j, room);
                                else
                                {
                                    if (d.CheckInBounds(x + i, y + j))
                                    {
                                        var c = d.cellData[x + i][y + j];
                                        if (c != null && (c.isExitCell || c.isDoorFrameCell)) ok = false;
                                    }
                                    // A one cell gap to any wall around the footprint.
                                    if (ok && margin && i >= -1 && i <= 2 && j >= -1 && j <= 2 && IsWall(d, x + i, y + j)) ok = false;
                                }
                            }
                        var spot = new UnityEngine.Vector2(x + 0.5f, y + 0.25f);
                        if (ok && (mover == null || BodyFits(mover, spot))) return spot;
                    }
            return null;
        }

        public static int Pulls;

        /// <summary>Count and log every Classic pull (host side), for the tests and for reports.</summary>
        static void PulledPostfix(PlayerController __instance, PlayerController other)
        {
            if (!Session.NetSession.Active) return;
            Pulls++;
            PullLock.Begin(__instance); // the host's own player (a guest's locks when the move reaches it)
            Plugin.Log.LogInfo($"PULL {(Game.PlayerSlots.IsRemote(__instance) ? "guest" : "host")} into {(other != null && other.CurrentRoom != null ? other.CurrentRoom.GetRoomName() : "?")} ghost={__instance.IsGhost}");
            if (Dev.DevEnv.IsHarness) __instance.StartCoroutine(CheckLanding(__instance));
        }

        /// <summary>Mark every remote player as ignored by this machine's camera.</summary>
        public static void Apply()
        {
            foreach (var kv in PlayerSlots.Remotes)
                if (kv.Value) PlayerSlots.SetIgnoredByCamera(kv.Value, true);
            if (GameManager.HasInstance && GameManager.Instance.MainCameraController != null)
                GameManager.Instance.MainCameraController.ClearPlayerCache();
        }
    }
}
