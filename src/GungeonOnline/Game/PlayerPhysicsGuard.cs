using System.Collections.Generic;
using HarmonyLib;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Each floor has its own physics. A player made while a floor was loading (a friend who reconnected then) could
    /// still be listed in the previous floor's physics; placing them on the new floor then failed inside the game's
    /// floor build, and the floor never finished loading. Before players are placed, any player the current
    /// physics does not know is registered with it fresh.
    /// </summary>
    internal static class PlayerPhysicsGuard
    {
        static readonly AccessTools.FieldRef<PhysicsEngine, List<SpeculativeRigidbody>> bodies =
            AccessTools.FieldRefAccess<PhysicsEngine, List<SpeculativeRigidbody>>("m_rigidbodies");

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Dungeonator.Dungeon), "PlacePlayerInRoom"),
                prefix: new HarmonyMethod(typeof(PlayerPhysicsGuard), nameof(Prefix)));
        }

        static void Prefix()
        {
            if (!PhysicsEngine.HasInstance || !GameManager.HasInstance) return;
            var engine = PhysicsEngine.Instance;
            var list = bodies(engine);
            foreach (var p in GameManager.Instance.AllPlayers)
            {
                var rb = p ? p.specRigidbody : null;
                if (rb == null || list.Contains(rb)) continue;
                rb.proxyId = -1;
                rb.PhysicsRegistration = SpeculativeRigidbody.RegistrationState.Deregistered;
                engine.Register(rb);
                Plugin.Log.LogInfo("PlayerPhysicsGuard: registered " + p.name + " with this floor's physics");
            }
        }
    }
}
