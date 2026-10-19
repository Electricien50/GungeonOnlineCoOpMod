using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Input;
using GungeonOnline.Net;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Input streams. Every machine samples its own player right after the device poll and sends at once
    /// (each packet repeats the last 8 frames, so a lost packet costs nothing). Each frame carries the sender's
    /// clock, so remote input is replayed on the sender's timeline: a player at 200 fps and one at 240 fps hold
    /// every input for the same real time on both machines, and network jitter does not shift dodge rolls.
    /// A small playout buffer absorbs jitter; Auto sizes it from measured jitter. Your own player is never delayed.
    /// </summary>
    internal sealed class InputSync : MonoBehaviour
    {
        public const int Redundancy = 8;

        public static InputSync Instance { get; private set; }

        public uint LocalSeq { get; private set; }
        readonly InputFrame[] localHistory = new InputFrame[256];
        readonly uint[] localTimeMs = new uint[256];

        public sealed class Stream
        {
            public int Slot;
            public uint NewestSeq, AppliedSeq;
            public InputFrame Last;
            public readonly SortedDictionary<uint, KeyValuePair<uint, InputFrame>> Pending = new SortedDictionary<uint, KeyValuePair<uint, InputFrame>>();
            // Clock mapping: sample = localArrivalMs - remoteMs. Min over a window = fastest path; spread = jitter.
            public readonly Queue<KeyValuePair<double, double>> Samples = new Queue<KeyValuePair<double, double>>();
            public double BaseOffsetMs = double.NaN;
            public double JitterMs;
            public double BufferMs;
            public int Starved, Merged, Applied;
            public double LastLateMs; // how long the applied frame waited after it was due
        }

        public readonly Dictionary<int, Stream> Streams = new Dictionary<int, Stream>();
        readonly WireWriter w = new WireWriter(512);

        static uint NowMs => (uint)(Time.realtimeSinceStartup * 1000.0);

        void Awake()
        {
            Instance = this;
            InputPump.BeforePoll += BeforePoll;
            InputPump.AfterPoll += AfterPoll;
        }

        void Start()
        {
            NetSession.Instance.On(Channel.Input, OnInput);
            NetSession.Instance.SessionEnded += () => { Streams.Clear(); LocalSeq = 0; };
            NetSession.Instance.PeerLeft += (c, why) => Streams.Remove(c.Slot);
            // A (re)connected peer may have restarted and count from zero again: start their streams fresh.
            NetSession.Instance.PeerJoined += c =>
            {
                if (NetSession.IsHost) Streams.Remove(c.Slot);
                else Streams.Clear();
            };
        }

        Stream Get(int slot)
        {
            if (!Streams.TryGetValue(slot, out var s)) Streams[slot] = s = new Stream { Slot = slot };
            return s;
        }

        /// <summary>Playout buffer for other players: the slider value, or Auto (-1) from measured jitter.</summary>
        static double TargetBufferMs(Stream s)
        {
            int setting = NetConfig.SmoothingMs.Value;
            if (setting >= 0) return setting;
            // Auto: cover nearly all jitter, never more than 40 ms, and at least one 240 Hz frame so bursts still play smoothly.
            return Mathf.Clamp((float)(s.JitterMs * 1.2 + 2.0), 4f, 40f);
        }

        void BeforePoll()
        {
            if (!NetSession.Active) return;
            NetSession.Instance.PollNow();
            foreach (var kv in PlayerSlots.Remotes)
            {
                var p = kv.Value;
                if (!p) continue;
                var dev = InputPump.Route(p.PlayerIDX);
                dev.Frame = Next(Get(kv.Key));
            }
        }

        /// <summary>
        /// Apply the newest frame whose sender time is due (local now - base offset - buffer). Any button pressed
        /// in a frame that is passed over is still pressed in the applied frame, so taps are never lost.
        /// No frame due: hold the last one (that is what the sender's player is doing too).
        /// </summary>
        static InputFrame Next(Stream s)
        {
            if (double.IsNaN(s.BaseOffsetMs)) return s.Last;
            s.BufferMs = TargetBufferMs(s);
            double dueRemote = NowMs - s.BaseOffsetMs - s.BufferMs;
            uint prevButtons = s.Last.Buttons, edges = 0;
            InputFrame chosen = s.Last;
            uint chosenSeq = s.AppliedSeq, chosenTime = 0;
            int n = 0;
            var used = new List<uint>();
            foreach (var kv in s.Pending)
            {
                if (kv.Value.Key > dueRemote) break;
                edges |= kv.Value.Value.Buttons & ~prevButtons;
                prevButtons = kv.Value.Value.Buttons;
                chosen = kv.Value.Value;
                chosenSeq = kv.Key;
                chosenTime = kv.Value.Key;
                used.Add(kv.Key);
                n++;
            }
            if (n == 0) { if (s.Applied > 0) s.Starved++; return s.Last; }
            foreach (var k in used) s.Pending.Remove(k);
            if (n > 1) s.Merged += n - 1;
            s.LastLateMs = dueRemote - chosenTime;
            chosen.Buttons |= edges;
            s.Last = chosen;
            s.AppliedSeq = chosenSeq;
            s.Applied++;
            return chosen;
        }

        void AfterPoll()
        {
            if (!NetSession.Active) return;
            var local = PlayerSlots.Local;
            var bi = BraveInput.GetInstanceForPlayer(0);
            if (local == null || bi == null) return;
            // While your pause menu is open the stick drives the menu, not your character.
            var f = Game.PauseScope.LocalMenuOpen ? default(InputFrame) : InputPump.Sample(bi.ActiveActions);
            // Mouse players have no aim stick: send the direction they are actually aiming as stick aim.
            if (!Game.PauseScope.LocalMenuOpen && bi.IsKeyboardAndMouse())
            {
                Vector2 dir = (Vector2)local.unadjustedAimPoint - local.CenterPosition;
                if (dir.sqrMagnitude > 0.0001f) f.Aim = dir.normalized;
                f.Flags |= NetFlags.MouseAim;
            }
            LocalSeq++;
            localHistory[LocalSeq & 255] = f;
            localTimeMs[LocalSeq & 255] = NowMs;
            WriteStream(NetSession.LocalSlot, LocalSeq);
            NetSession.Instance.Node.Broadcast((byte)Channel.Input, w.ToArray(), Delivery.Unreliable);
            NetSession.Instance.FlushNow(); // out the door this frame
        }

        void WriteStream(int slot, uint newest)
        {
            w.Reset();
            w.U8((byte)slot);
            w.U32(newest);
            uint newestTime = localTimeMs[newest & 255];
            w.U32(newestTime);
            int count = (int)System.Math.Min(newest, Redundancy);
            w.U8((byte)count);
            using (var ms = new System.IO.MemoryStream(count * (InputFrame.WireSize + 2)))
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                for (int i = 0; i < count; i++)
                {
                    uint seq = newest - (uint)i;
                    bw.Write((ushort)System.Math.Min(65535u, newestTime - localTimeMs[seq & 255]));
                    localHistory[seq & 255].Write(bw);
                }
                bw.Flush();
                var b = ms.ToArray();
                w.Bytes(b, 0, b.Length);
            }
        }

        void OnInput(Connection c, byte[] data, Delivery d)
        {
            var r = new WireReader(data, 0, data.Length);
            int slot = r.U8();
            uint newest = r.U32();
            uint newestTime = r.U32();
            int count = r.U8();
            if (NetSession.IsHost && slot != c.Slot) return; // a client may only speak for itself
            if (slot == NetSession.LocalSlot) return;
            var s = Get(slot);
            double now = NowMs;
            Observe(s, now, newestTime, newest > s.NewestSeq);
            using (var ms = new System.IO.MemoryStream(data, r.Pos, data.Length - r.Pos))
            using (var br = new System.IO.BinaryReader(ms))
            {
                for (int i = 0; i < count; i++)
                {
                    uint seq = newest - (uint)i;
                    uint t = newestTime - br.ReadUInt16();
                    var f = InputFrame.Read(br);
                    if (seq <= s.AppliedSeq || s.Pending.ContainsKey(seq)) continue;
                    s.Pending[seq] = new KeyValuePair<uint, InputFrame>(t, f);
                }
            }
            if (newest > s.NewestSeq) s.NewestSeq = newest;
            if (NetSession.IsHost)
                foreach (var other in NetSession.Instance.Node.Connections)
                    if (other != c) other.Send((byte)Channel.Input, data, Delivery.Unreliable);
        }

        /// <summary>Track the clock offset (fastest arrival over 3 s) and jitter (90th percentile above it).</summary>
        static void Observe(Stream s, double nowMs, uint remoteMs, bool fresh)
        {
            if (!fresh) return;
            double sample = nowMs - remoteMs;
            s.Samples.Enqueue(new KeyValuePair<double, double>(nowMs, sample));
            while (s.Samples.Count > 0 && nowMs - s.Samples.Peek().Key > 3000) s.Samples.Dequeue();
            double min = double.MaxValue;
            var list = new List<double>(s.Samples.Count);
            foreach (var kv in s.Samples) { if (kv.Value < min) min = kv.Value; list.Add(kv.Value); }
            list.Sort();
            double p90 = list[(int)(list.Count * 0.9)];
            s.BaseOffsetMs = min;
            s.JitterMs = p90 - min;
        }
    }
}
