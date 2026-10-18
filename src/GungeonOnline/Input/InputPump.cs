using System;
using System.Collections.Generic;
using HarmonyLib;
using InControl;

namespace GungeonOnline.Input
{
    /// <summary>
    /// Routes player slots to virtual devices and feeds them right before InControl polls devices,
    /// so a frame that arrived this Unity frame is used this Unity frame (no added frame of delay).
    /// </summary>
    internal static class InputPump
    {
        static readonly Dictionary<int, NetInputDevice> devices = new Dictionary<int, NetInputDevice>();
        static readonly Dictionary<int, GungeonActions> bound = new Dictionary<int, GungeonActions>();

        /// <summary>Called first thing in every InControl update. Sources set device frames here.</summary>
        public static event Action BeforePoll;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(InputManager), "UpdateInternal"),
                prefix: new HarmonyMethod(typeof(InputPump), nameof(Prefix)));
        }

        static void Prefix()
        {
            try { BeforePoll?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogError("InputPump: " + e); }
        }

        /// <summary>Give player <paramref name="playerId"/> a virtual device. Returns it.</summary>
        public static NetInputDevice Route(int playerId)
        {
            if (!devices.TryGetValue(playerId, out var d))
            {
                d = new NetInputDevice(playerId);
                InputManager.AttachDevice(d);
                devices[playerId] = d;
                Plugin.Log.LogInfo($"InputPump: player {playerId} -> virtual device");
            }
            return d;
        }

        public static void Unroute(int playerId)
        {
            if (devices.TryGetValue(playerId, out var d))
            {
                InputManager.DetachDevice(d);
                devices.Remove(playerId);
                bound.Remove(playerId);
            }
        }

        public static NetInputDevice DeviceFor(int playerId) => devices.TryGetValue(playerId, out var d) ? d : null;

        /// <summary>
        /// Keeps each routed player's action set pointed at its virtual device with the fixed bindings.
        /// The game rebuilds action sets on controller changes, so this is checked every frame (cheap).
        /// </summary>
        public static void EnforceRouting()
        {
            foreach (var kv in devices)
            {
                var bi = BraveInput.GetInstanceForPlayer(kv.Key);
                var actions = bi != null ? bi.ActiveActions : null;
                if (actions == null) continue;
                if (!bound.TryGetValue(kv.Key, out var prev) || !ReferenceEquals(prev, actions))
                {
                    NetBindings.Apply(actions);
                    bound[kv.Key] = actions;
                    Plugin.Log.LogInfo($"InputPump: bound player {kv.Key} action set");
                }
                if (actions.Device != kv.Value) actions.Device = kv.Value;
                if (actions.ForceDisable) actions.ForceDisable = false;
            }
        }
    }
}
