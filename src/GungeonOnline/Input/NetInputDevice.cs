using InControl;

namespace GungeonOnline.Input
{
    /// <summary>
    /// A virtual controller driven by network (or scripted) input. One per remote player slot.
    /// InControl polls it in its own update; <see cref="InputPump"/> makes sure the newest frame is set
    /// before that poll in the same Unity frame.
    /// </summary>
    public sealed class NetInputDevice : InputDevice
    {
        public InputFrame Frame;
        public readonly int Slot;

        public NetInputDevice(int slot) : base("Gungeon Online Remote " + slot)
        {
            Slot = slot;
            DeviceClass = InputDeviceClass.Controller;
            DeviceStyle = InputDeviceStyle.XboxOne;
            foreach (var t in new[]
            {
                InputControlType.LeftStickLeft, InputControlType.LeftStickRight, InputControlType.LeftStickUp, InputControlType.LeftStickDown,
                InputControlType.RightStickLeft, InputControlType.RightStickRight, InputControlType.RightStickUp, InputControlType.RightStickDown,
                NetBindings.SelectDownControl,
            })
                AddControl(t, t.ToString(), 0f, 1f);
            foreach (var kv in NetBindings.Map)
                AddControl(kv.Value.Value, kv.Key, 0f, 1f);
        }

        public override void Update(ulong updateTick, float deltaTime)
        {
            var f = Frame;
            var m = f.Move;
            var a = f.Aim;
            UpdateWithValue(InputControlType.LeftStickLeft, m.x < 0 ? -m.x : 0f, updateTick, deltaTime);
            UpdateWithValue(InputControlType.LeftStickRight, m.x > 0 ? m.x : 0f, updateTick, deltaTime);
            UpdateWithValue(InputControlType.LeftStickUp, m.y > 0 ? m.y : 0f, updateTick, deltaTime);
            UpdateWithValue(InputControlType.LeftStickDown, m.y < 0 ? -m.y : 0f, updateTick, deltaTime);
            UpdateWithValue(InputControlType.RightStickLeft, a.x < 0 ? -a.x : 0f, updateTick, deltaTime);
            UpdateWithValue(InputControlType.RightStickRight, a.x > 0 ? a.x : 0f, updateTick, deltaTime);
            UpdateWithValue(InputControlType.RightStickUp, a.y > 0 ? a.y : 0f, updateTick, deltaTime);
            UpdateWithValue(InputControlType.RightStickDown, a.y < 0 ? -a.y : 0f, updateTick, deltaTime);
            UpdateWithState(NetBindings.SelectDownControl, (f.Flags & NetFlags.SelectDown) != 0, updateTick, deltaTime);
            foreach (var kv in NetBindings.Map)
                UpdateWithState(kv.Value.Value, f.Has(kv.Value.Key), updateTick, deltaTime);
        }
    }
}
