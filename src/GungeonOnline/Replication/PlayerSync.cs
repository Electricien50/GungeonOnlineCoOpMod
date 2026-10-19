using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Player state. The host sends every player's authoritative position each frame, tagged with the newest
    /// input sequence it had applied for that player. Each machine compares that with where its own copy of the
    /// player was after the same input and removes the difference: small errors are blended out over a few
    /// frames through the physics engine, large ones snap. Your own player is never delayed by the network.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    internal sealed class PlayerSync : MonoBehaviour
    {
        public static PlayerSync Instance { get; private set; }

        const float SnapDistance = 2.0f;     // units; beyond this, teleport
        const float IgnoreDistance = 0.04f;  // under one pixel-ish; leave alone

        sealed class Track
        {
            public readonly Dictionary<uint, Vector2> History = new Dictionary<uint, Vector2>();
            public Vector2 PendingCorrection;
            public uint LastReconciledSeq;
            public float LastError, MaxError;
            public int Snaps, Corrections;
        }

        readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        uint hostFrame;
        uint lastSnapshotFrame;
        public int SnapshotsApplied;
        readonly WireWriter w = new WireWriter(512);

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.On(Channel.Snapshot, OnSnapshot);
            NetSession.Instance.SessionEnded += () => { tracks.Clear(); lastSnapshotFrame = 0; hostFrame = 0; };
            // After a reconnect the other side may have restarted its counters; start reconciliation fresh.
            NetSession.Instance.PeerJoined += c => { tracks.Clear(); lastSnapshotFrame = 0; };
        }

        /// <summary>Floor change: positions jump (everyone starts at the new entrance), so start reconciliation fresh.</summary>
        public void ResetTracks() { tracks.Clear(); }

        Track T(int slot)
        {
            if (!tracks.TryGetValue(slot, out var t)) tracks[slot] = t = new Track();
            return t;
        }

        public string Stats(int slot) =>
            tracks.TryGetValue(slot, out var t) ? $"err={t.LastError:F3} max={t.MaxError:F3} corr={t.Corrections} snaps={t.Snaps}" : "-";

        uint SeqFor(int slot)
        {
            if (slot == NetSession.LocalSlot) return InputSync.Instance.LocalSeq;
            return InputSync.Instance.Streams.TryGetValue(slot, out var s) ? s.AppliedSeq : 0;
        }

        /// <summary>End of frame: record positions (all machines), send snapshot (host).</summary>
        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try { LateUpdateImpl(); }
            finally { Dev.NetProfiler.End("players", t0); }
        }

        void LateUpdateImpl()
        {
            if (!NetSession.Active) return;
            Camera.IndependentCamera.Apply();
            ApplyPendingCorrections();
            RecordHistory();
            if (NetSession.IsHost) SendSnapshot();
        }

        void RecordHistory()
        {
            Record(NetSession.LocalSlot, PlayerSlots.Local);
            foreach (var kv in PlayerSlots.Remotes) Record(kv.Key, kv.Value);
        }

        void Record(int slot, PlayerController p)
        {
            if (!p) return;
            uint seq = SeqFor(slot);
            if (seq == 0) return;
            var t = T(slot);
            // Record where the player will be once outstanding corrections land, so they are not counted twice.
            t.History[seq] = (Vector2)p.transform.position + t.PendingCorrection;
            if (t.History.Count > 600)
            {
                var old = new List<uint>();
                foreach (var k in t.History.Keys) if (k + 300 < seq) old.Add(k);
                foreach (var k in old) t.History.Remove(k);
            }
        }

        void SendSnapshot()
        {
            hostFrame++;
            w.Reset();
            w.U32(hostFrame);
            var list = new List<KeyValuePair<int, PlayerController>>();
            if (PlayerSlots.Local) list.Add(new KeyValuePair<int, PlayerController>(NetSession.LocalSlot, PlayerSlots.Local));
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) list.Add(kv);
            w.U8((byte)list.Count);
            foreach (var kv in list)
            {
                var p = kv.Value;
                w.U8((byte)kv.Key);
                w.U32(SeqFor(kv.Key));
                Vector2 pos = p.transform.position;
                w.F32(pos.x); w.F32(pos.y);
            }
            NetSession.Instance.Node.Broadcast((byte)Channel.Snapshot, w.ToArray(), Delivery.Unreliable);
        }

        void OnSnapshot(Connection c, byte[] data, Delivery d)
        {
            if (NetSession.IsHost) return;
            var r = new WireReader(data, 0, data.Length);
            uint frame = r.U32();
            if (frame <= lastSnapshotFrame) return; // older than what we have: drop
            lastSnapshotFrame = frame;
            SnapshotsApplied++;
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                int slot = r.U8();
                uint seq = r.U32();
                var pos = new Vector2(r.F32(), r.F32());
                var p = PlayerSlots.ForSlot(slot);
                if (!p || seq == 0) continue;
                Reconcile(slot, p, seq, pos);
            }
        }

        void Reconcile(int slot, PlayerController p, uint seq, Vector2 hostPos)
        {
            var t = T(slot);
            if (seq <= t.LastReconciledSeq) return;
            if (p.specRigidbody == null || GameManager.Instance.IsLoadingLevel) return;
            if (!t.History.TryGetValue(seq, out var mine))
            {
                // No record for that input (after a load, a warp, a stalled stream): if we are far off, go there.
                var off = hostPos - (Vector2)p.transform.position;
                if (off.magnitude > SnapDistance * 2f)
                {
                    t.Snaps++;
                    t.PendingCorrection = Vector2.zero;
                    t.History.Clear();
                    t.LastReconciledSeq = seq;
                    p.transform.position = new Vector3(hostPos.x, hostPos.y, p.transform.position.z);
                    try { p.specRigidbody.Reinitialize(); } catch { } // player still being set up after a load
                    Plugin.Log.LogInfo($"SYNC slot={slot} hard snap {off.magnitude:F1} (no history for {seq})");
                }
                return;
            }
            t.LastReconciledSeq = seq;
            var err = hostPos - mine;
            t.LastError = err.magnitude;
            if (t.LastError > t.MaxError) t.MaxError = t.LastError;
            if (t.LastError < IgnoreDistance) return;
            // Everything recorded after seq carries the same error; shift it so the next snapshot does not count it twice.
            var keys = new List<uint>(t.History.Keys);
            foreach (var k in keys) if (k > seq) t.History[k] += err;
            if (t.LastError > 0.25f)
                Plugin.Log.LogInfo($"SYNC slot={slot} seq={seq} roll={p.IsDodgeRolling} err={t.LastError:F3} host={hostPos.x:F2},{hostPos.y:F2} mine={mine.x:F2},{mine.y:F2} t={Time.realtimeSinceStartup:F2}");
            if (t.LastError > SnapDistance)
            {
                t.Snaps++;
                t.PendingCorrection = Vector2.zero;
                p.transform.position = p.transform.position + (Vector3)err;
                p.specRigidbody.Reinitialize();
            }
            else
            {
                t.Corrections++;
                t.PendingCorrection += err;
            }
        }

        /// <summary>Blend outstanding corrections in over a few frames.</summary>
        void ApplyPendingCorrections()
        {
            foreach (var kv in tracks)
            {
                var t = kv.Value;
                if (t.PendingCorrection.sqrMagnitude < 1e-6f) continue;
                var p = PlayerSlots.ForSlot(kv.Key);
                if (!p || p.specRigidbody == null || GameManager.Instance.IsLoadingLevel) { t.PendingCorrection = Vector2.zero; continue; }
                var step = t.PendingCorrection * NetConfig.CorrectionSpeed.Value;
                if (t.PendingCorrection.magnitude < 0.1f) step = t.PendingCorrection;
                // Move directly: the target is where the host's physics put this player, so it is a legal spot.
                p.transform.position = p.transform.position + (Vector3)step;
                p.specRigidbody.Reinitialize();
                t.PendingCorrection -= step;
            }
        }
    }
}
