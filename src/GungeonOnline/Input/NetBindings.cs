using System;
using System.Collections.Generic;
using InControl;

namespace GungeonOnline.Input
{
    /// <summary>
    /// Fixed binding layout for a remote player's action set: every Gungeon action gets its own private
    /// control on <see cref="NetInputDevice"/>, so nothing in the host's options can collide with it.
    /// </summary>
    internal static class NetBindings
    {
        /// <summary>Gungeon action field name, semantic button, private control.</summary>
        internal static readonly List<KeyValuePair<string, KeyValuePair<NetButton, InputControlType>>> Map = Build();

        static List<KeyValuePair<string, KeyValuePair<NetButton, InputControlType>>> Build()
        {
            var controls = new List<InputControlType>();
            for (int i = 0; i <= 19; i++) controls.Add(InputControlType.Button0 + i);
            for (int i = 1; i <= 12; i++) controls.Add((InputControlType)Enum.Parse(typeof(InputControlType), "Action" + i));

            string[] actions =
            {
                "ShootAction", "DodgeRollAction", "InteractAction", "ReloadAction", "UseItemAction", "MapAction",
                "GunUpAction", "GunDownAction", "ItemUpAction", "ItemDownAction", "KeybulletAction", "PauseAction",
                "CancelAction", "MenuSelectAction", "EquipmentMenuAction", "BlankAction", "DropGunAction",
                "DropItemAction", "GunQuickEquipAction", "MinimapZoomInAction", "MinimapZoomOutAction",
                "SwapDualGunsAction", "PunchoutDodgeLeft", "PunchoutDodgeRight", "PunchoutBlock", "PunchoutDuck",
                "PunchoutPunchLeft", "PunchoutPunchRight", "PunchoutSuper", "SelectLeft", "SelectRight", "SelectUp",
            };
            var list = new List<KeyValuePair<string, KeyValuePair<NetButton, InputControlType>>>();
            for (int i = 0; i < actions.Length; i++)
                list.Add(new KeyValuePair<string, KeyValuePair<NetButton, InputControlType>>(
                    actions[i], new KeyValuePair<NetButton, InputControlType>((NetButton)(1u << i), controls[i])));
            return list;
        }

        static readonly HarmonyLib.AccessTools.FieldRef<PlayerAction, List<BindingSourceType>> ignored =
            HarmonyLib.AccessTools.FieldRefAccess<PlayerAction, List<BindingSourceType>>("m_ignoredBindingSources");

        internal const InputControlType SelectDownControl = InputControlType.DPadDown;

        static readonly Dictionary<string, System.Reflection.FieldInfo> fields = new Dictionary<string, System.Reflection.FieldInfo>();

        internal static PlayerAction Action(GungeonActions a, string name) => Get(a, name);

        static PlayerAction Get(GungeonActions a, string name)
        {
            if (!fields.TryGetValue(name, out var f))
                fields[name] = f = typeof(GungeonActions).GetField(name);
            return f?.GetValue(a) as PlayerAction;
        }

        static void Bind(GungeonActions a, string name, InputControlType c)
        {
            var act = Get(a, name);
            if (act == null) { Plugin.Log.LogWarning("NetBindings: no action " + name); return; }
            act.ClearBindings();
            // The game tells non-primary action sets to ignore controller bindings when it thinks no pad is
            // assigned; this set is driven by a virtual pad, so lift that.
            ignored(act).Clear();
            act.AddBinding(new DeviceBindingSource(c));
        }

        public static void Apply(GungeonActions a)
        {
            Bind(a, "Left", InputControlType.LeftStickLeft);
            Bind(a, "Right", InputControlType.LeftStickRight);
            Bind(a, "Up", InputControlType.LeftStickUp);
            Bind(a, "Down", InputControlType.LeftStickDown);
            Bind(a, "AimLeft", InputControlType.RightStickLeft);
            Bind(a, "AimRight", InputControlType.RightStickRight);
            Bind(a, "AimUp", InputControlType.RightStickUp);
            Bind(a, "AimDown", InputControlType.RightStickDown);
            foreach (var kv in Map) Bind(a, kv.Key, kv.Value.Value);
            Bind(a, "SelectDown", SelectDownControl);
        }
    }
}
