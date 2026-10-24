using System.Collections.Generic;
using Dungeonator;
using FullInspector;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Every machine builds each floor itself from the host's seed, but the game also reads the save while it does:
    /// story and NPC flags, what you have met before, lifetime stats, the run's plan of special rooms, curse and
    /// master rounds. Two players with different saves built different floors (even without other mods) and the
    /// guest was sent back to the Breach. The host captures that state as each floor starts; the guest builds that
    /// floor with it, so both build the same one.
    /// </summary>
    internal static class GenContext
    {
        public const byte Msg = 6; // RunSync control message type

        // Host: the state at the start of the floor we are on, resent unchanged to anyone who rejoins it.
        static byte[] hostMessage;

        // Guest: the host's state for one floor.
        sealed class Snapshot
        {
            public int Seed, Index, Curse, Tokens;
            public GameStatsManager Stats;
            public byte[] Plan;
            public bool PlanApplied;
        }
        static Snapshot latest;

        static int depth;
        static GameStatsManager ownStats;
        static int ownTokens;
        static readonly AccessTools.FieldRef<object, int> tokensField =
            AccessTools.FieldRefAccess<int>(typeof(PlayerController), "m_masteryTokensCollectedThisRun");
        static readonly System.Reflection.FieldInfo statsInstance = AccessTools.Field(typeof(GameStatsManager), "m_instance");

        public static bool Active => depth > 0;
        public static int Applied, Captured, LastBytes;

        static bool IsGuest => NetSession.Active && !NetSession.IsHost;

        public static void Install(Harmony h)
        {
            var t = typeof(GenContext);
            // A floor is built in steps: the layout during the loading screen (one step per frame), then the rooms.
            // Each step runs with the host's state; frames in between see our own.
            var build = new HarmonyMethod(t, nameof(BuildPrefix));
            var leave = new HarmonyMethod(t, nameof(Leave));
            h.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(LoopDungeonGenerator), nameof(LoopDungeonGenerator.GenerateDungeonLayoutDeferred))), prefix: build, finalizer: leave);
            h.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Dungeon), nameof(Dungeon.Regenerate))), prefix: build, finalizer: leave);
            h.Patch(AccessTools.Method(typeof(GameLevelDefinition), nameof(GameLevelDefinition.LovinglySelectDungeonFlow)), prefix: build, finalizer: leave);
            // The floor's flow is picked when the generator is made (the Forge's depends on a story flag).
            foreach (var ctor in typeof(LoopDungeonGenerator).GetConstructors()) h.Patch(ctor, prefix: build, finalizer: leave);
            // The one save write made while a floor loads: this run's stats, which stay ours.
            h.Patch(AccessTools.Method(typeof(GameStatsManager), nameof(GameStatsManager.MoveSessionStatsToSavedSessionStats)), prefix: new HarmonyMethod(t, nameof(OwnSessionPrefix)));
            // Setting the run seed plans the run's special rooms on the spot, from the save.
            h.Patch(AccessTools.PropertySetter(typeof(GameManager), nameof(GameManager.CurrentRunSeed)),
                prefix: new HarmonyMethod(t, nameof(SeedPrefix)), finalizer: new HarmonyMethod(t, nameof(Leave)));
            h.Patch(AccessTools.Method(typeof(PlayerStats), nameof(PlayerStats.GetTotalCurse)), prefix: new HarmonyMethod(t, nameof(CursePrefix)));
            if (Dev.DevEnv.IsHarness)
            {
                off = System.Environment.GetEnvironmentVariable("GO_GENCONTEXT") == "off";
                // Tests: a save far from the host's (every story flag set), the way two real players' saves differ.
                if (System.Environment.GetEnvironmentVariable("GO_GEN_TRACE") == "1")
                {
                    h.Patch(AccessTools.Method(typeof(DungeonPrerequisite), nameof(DungeonPrerequisite.CheckConditionsFulfilled)), postfix: new HarmonyMethod(t, nameof(TracePrereq)));
                    foreach (var m in typeof(GameStatsManager).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                        if ((m.Name.StartsWith("Get") || m.Name.StartsWith("Query") || m.Name.StartsWith("Any") || m.Name.StartsWith("get_")) && m.ReturnType != typeof(void) && !m.IsGenericMethod)
                            try { h.Patch(m, postfix: new HarmonyMethod(t, nameof(TraceQuery))); } catch (System.Exception) { }
                }
                if (System.Environment.GetEnvironmentVariable("GO_SAVE_BIG") == "1" && (Dev.DevEnv.Auto == "host" || Dev.DevEnv.Auto == "resume"))
                    h.Patch(AccessTools.Method(typeof(GameStatsManager), nameof(GameStatsManager.Load)), postfix: new HarmonyMethod(t, nameof(BigSave)));
                if (System.Environment.GetEnvironmentVariable("GO_SAVE_SKEW") == "1" && Dev.DevEnv.Auto != "host" && Dev.DevEnv.Auto != "resume")
                    h.Patch(AccessTools.Method(typeof(GameStatsManager), nameof(GameStatsManager.Load)), postfix: new HarmonyMethod(t, nameof(SkewSave)));
            }
        }

        static bool off;

        // Tests (GO_GEN_TRACE=1): every save check made while a floor is built, to compare host and guest.
        static void TracePrereq(DungeonPrerequisite __instance, bool __result)
        {
            if (!GameManager.HasInstance || !(Dungeon.IsGenerating || GameManager.Instance.IsLoadingLevel)) return;
            Plugin.Log.LogInfo($"GENTRACE prereq {__instance.prerequisiteType} {__instance.saveFlagToCheck} {__instance.statToCheck} {__instance.encounteredObjectGuid} -> {__result} active={Active} {Caller()}");
        }

        static void TraceQuery(System.Reflection.MethodBase __originalMethod, object[] __args, object __result)
        {
            if (!GameManager.HasInstance || !(Dungeon.IsGenerating || GameManager.Instance.IsLoadingLevel)) return;
            string a = __args != null && __args.Length > 0 && __args[0] != null ? (__args[0] is Object o && o ? o.name : __args[0].ToString()) : "";
            Plugin.Log.LogInfo($"GENTRACE {__originalMethod.Name} {a} -> {__result} active={Active} {Caller()}");
        }

        static string Caller()
        {
            var st = new System.Diagnostics.StackTrace(2, false);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < st.FrameCount && i < 4; i++)
            {
                var m = st.GetFrame(i).GetMethod();
                if (m != null) sb.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?").Append('.').Append(m.Name).Append('<');
            }
            return sb.ToString();
        }

        static void BigSave()
        {
            // A long-played save: every encounter and room seen several times.
            var gsm = GameStatsManager.Instance;
            var tr = Traverse.Create(gsm);
            var enc = tr.Field("m_encounteredTrackables").GetValue<Dictionary<string, EncounteredObjectData>>();
            int n = 0;
            foreach (var e in EncounterDatabase.Instance.Entries)
                if (e != null && !string.IsNullOrEmpty(e.myGuid) && !enc.ContainsKey(e.myGuid)) { enc[e.myGuid] = new EncounteredObjectData { encounterCount = 7 }; n++; }
            foreach (var room in Resources.FindObjectsOfTypeAll<PrototypeDungeonRoom>())
                if (room && !string.IsNullOrEmpty(room.GUID) && !gsm.m_encounteredRooms.ContainsKey(room.GUID)) { gsm.m_encounteredRooms[room.GUID] = new EncounteredObjectData { encounterCount = 3 }; n++; }
            Plugin.Log.LogInfo("Harness: big save, " + n + " entries added");
        }

        static void SkewSave()
        {
            var gsm = GameStatsManager.Instance;
            int n = 0;
            foreach (GungeonFlags f in System.Enum.GetValues(typeof(GungeonFlags)))
                if (f != GungeonFlags.NONE && gsm.m_flags.Add(f)) n++;
            Plugin.Log.LogInfo("Harness: save skewed, " + n + " flags set");
        }

        // ---------------- Host ----------------

        /// <summary>Host: record this floor's starting state and send it ahead of the load order.</summary>
        public static void CaptureAndSend(int seed, int index)
        {
            hostMessage = null;
            try { hostMessage = Build(seed, index); Captured++; }
            catch (System.Exception e) { Plugin.Log.LogWarning("GenContext: could not capture floor state: " + e.Message); }
            if (hostMessage != null) NetSession.Instance.Node.Broadcast((byte)Channel.Control, hostMessage, Delivery.ReliableOrdered);
        }

        /// <summary>Host: the current floor's starting state to someone who rejoins it.</summary>
        public static void SendTo(Connection c)
        {
            if (hostMessage != null) c.Send((byte)Channel.Control, hostMessage, Delivery.ReliableOrdered);
        }

        public static void ClearHost() => hostMessage = null;

        static byte[] Build(int seed, int index)
        {
            var gsm = GameStatsManager.Instance;
            var body = new WireWriter(64 * 1024);
            var p = GameManager.Instance.PrimaryPlayer;
            body.I32(PlayerStats.GetTotalCurse());
            body.I32(p ? p.MasteryTokensCollectedThisRun : 0);
            bool pretty = fiSettings.PrettyPrintSerializedJson;
            fiSettings.PrettyPrintSerializedJson = false;
            try
            {
                body.Str(SerializationHelpers.SerializeToContent<GameStatsManager, FullSerializerSerializer>(gsm));
                // The current run's stats are not part of the save; copy them as well.
                var tr = Traverse.Create(gsm);
                var session = tr.Field("m_sessionStats").GetValue<GameStats>();
                body.Bool(session != null);
                if (session != null)
                {
                    body.I32((int)tr.Field("m_sessionCharacter").GetValue<PlayableCharacters>());
                    body.Str(SerializationHelpers.SerializeToContent<GameStats, FullSerializerSerializer>(session));
                    var saved = tr.Field("m_savedSessionStats").GetValue<GameStats>();
                    body.Str(saved != null ? SerializationHelpers.SerializeToContent<GameStats, FullSerializerSerializer>(saved) : "");
                    var syn = tr.Field("m_sessionSynergies").GetValue<HashSet<int>>();
                    body.VarU((uint)(syn != null ? syn.Count : 0));
                    if (syn != null) foreach (var s in syn) body.I32(s);
                }
            }
            finally { fiSettings.PrettyPrintSerializedJson = pretty; }
            WritePlan(body);
            var raw = body.ToArray();
            var packed = Compression.Pack(raw);
            var w = new WireWriter(packed.Length + 32);
            w.U8(Msg); w.I32(seed); w.I32(index);
            w.Bool(packed != raw);
            w.VarU((uint)packed.Length); w.Bytes(packed, 0, packed.Length);
            LastBytes = w.Length;
            Plugin.Log.LogInfo($"GenContext: floor {index} state {raw.Length} bytes, sent {w.Length}");
            return w.ToArray();
        }

        // The run's plan: which special room sets go on which floors, which once-per-run sets were used.
        static void WritePlan(WireWriter w)
        {
            w.Bool(MetaInjectionData.BlueprintGenerated);
            w.Bool(MetaInjectionData.CastleExcludedSetUsed);
            w.Bool(MetaInjectionData.CellGeneratedForCurrentBlueprint);
            w.Bool(MetaInjectionData.ForceEarlyChest);
            WriteCounts(w, MetaInjectionData.KeybulletsAssignedToFloors);
            WriteCounts(w, MetaInjectionData.ChanceBulletsAssignedToFloors);
            WriteCounts(w, MetaInjectionData.WallMimicsAssignedToFloors);
            var bp = MetaInjectionData.CurrentRunBlueprint;
            w.VarU((uint)(bp != null ? bp.Count : 0));
            if (bp != null)
                foreach (var kv in bp)
                {
                    w.I32((int)kv.Key);
                    w.VarU((uint)kv.Value.Count);
                    foreach (var m in kv.Value)
                    {
                        w.Str(m.injectionData ? m.injectionData.name : "");
                        w.Bool(m.forceSecret);
                        w.VarU((uint)m.SucceededRandomizationCheckMap.Count);
                        foreach (var c in m.SucceededRandomizationCheckMap) { WriteModifier(w, c.Key); w.Bool(c.Value); }
                    }
                }
            var used = MetaInjectionData.InjectionSetsUsedThisRun;
            w.VarU((uint)(used != null ? used.Count : 0));
            if (used != null) foreach (var m in used) WriteModifier(w, m);
        }

        static void WriteCounts(WireWriter w, Dictionary<GlobalDungeonData.ValidTilesets, int> d)
        {
            w.VarU((uint)d.Count);
            foreach (var kv in d) { w.I32((int)kv.Key); w.I32(kv.Value); }
        }

        // ---------------- Guest ----------------

        /// <summary>Guest: the host's state for a floor that is about to load.</summary>
        public static void Receive(ref WireReader r)
        {
            int seed = r.I32(), index = r.I32();
            bool packed = r.Bool();
            var data = r.Bytes((int)r.VarU());
            try
            {
                var raw = packed ? Compression.Unpack(data) : data;
                var b = new WireReader(raw, 0, raw.Length);
                var s = new Snapshot { Seed = seed, Index = index, Curse = b.I32(), Tokens = b.I32() };
                s.Stats = SerializationHelpers.DeserializeFromContent<GameStatsManager, FullSerializerSerializer>(b.LongStr());
                if (s.Stats.huntProgress == null) s.Stats.huntProgress = new MonsterHuntProgress();
                if (b.Bool())
                {
                    var tr = Traverse.Create(s.Stats);
                    tr.Field("m_sessionCharacter").SetValue((PlayableCharacters)b.I32());
                    tr.Field("m_sessionStats").SetValue(SerializationHelpers.DeserializeFromContent<GameStats, FullSerializerSerializer>(b.LongStr()));
                    string saved = b.LongStr();
                    tr.Field("m_savedSessionStats").SetValue(saved.Length > 0 ? SerializationHelpers.DeserializeFromContent<GameStats, FullSerializerSerializer>(saved) : new GameStats());
                    var syn = new HashSet<int>();
                    for (uint i = b.VarU(); i > 0; i--) syn.Add(b.I32());
                    tr.Field("m_sessionSynergies").SetValue(syn);
                }
                s.Plan = b.Bytes(b.Remaining);
                latest = s;
                Plugin.Log.LogInfo($"GenContext: host floor {index} state received ({data.Length} bytes)");
            }
            catch (System.Exception e)
            {
                latest = null;
                Plugin.Log.LogWarning("GenContext: host floor state unreadable, building from our own save: " + e.Message);
            }
        }

        public static void Forget() { latest = null; }

        static bool Matches(int seed) => IsGuest && !off && latest != null && latest.Seed == seed;

        static void SeedPrefix(int value, ref bool __state)
        {
            __state = false;
            if (Matches(value)) { Enter(); __state = true; }
        }

        static void BuildPrefix(ref bool __state)
        {
            __state = false;
            if (!GameManager.HasInstance) return;
            // Only the floor the host described (a guest following a reset or rejoin may build another first).
            if (!Matches(GameManager.Instance.CurrentRunSeed) || latest.Index != RunSync.CurrentIndex || RunSync.CurrentIndex < 0) return;
            if (!latest.PlanApplied)
            {
                latest.PlanApplied = true;
                try { ReadPlan(latest.Plan); Applied++; Plugin.Log.LogInfo($"GenContext: building floor {latest.Index} with the host's save state"); }
                catch (System.Exception e) { Plugin.Log.LogWarning("GenContext: run plan not applied: " + e.Message); }
            }
            Enter();
            __state = true;
        }

        static bool OwnSessionPrefix(GameStatsManager __instance)
        {
            if (Active && ownStats != null && __instance != ownStats)
            {
                ownStats.MoveSessionStatsToSavedSessionStats();
                return false;
            }
            return true;
        }

        static void Enter()
        {
            if (depth++ > 0) return;
            ownStats = (GameStatsManager)statsInstance.GetValue(null);
            statsInstance.SetValue(null, latest.Stats);
            var local = PlayerSlots.Local;
            if (local) { ownTokens = tokensField(local); tokensField(local) = latest.Tokens; }
        }

        static System.Exception Leave(System.Exception __exception, bool __state)
        {
            if (!__state || depth == 0) return __exception;
            if (--depth > 0) return __exception;
            statsInstance.SetValue(null, ownStats);
            ownStats = null;
            var local = PlayerSlots.Local;
            if (local) tokensField(local) = ownTokens;
            return __exception;
        }

        static bool CursePrefix(ref int __result)
        {
            if (!Active || latest == null) return true;
            __result = latest.Curse;
            return false;
        }

        static void ReadPlan(byte[] plan)
        {
            var r = new WireReader(plan, 0, plan.Length);
            MetaInjectionData.BlueprintGenerated = r.Bool();
            MetaInjectionData.CastleExcludedSetUsed = r.Bool();
            MetaInjectionData.CellGeneratedForCurrentBlueprint = r.Bool();
            MetaInjectionData.ForceEarlyChest = r.Bool();
            ReadCounts(ref r, MetaInjectionData.KeybulletsAssignedToFloors);
            ReadCounts(ref r, MetaInjectionData.ChanceBulletsAssignedToFloors);
            ReadCounts(ref r, MetaInjectionData.WallMimicsAssignedToFloors);
            var bp = MetaInjectionData.CurrentRunBlueprint ?? (MetaInjectionData.CurrentRunBlueprint = new Dictionary<GlobalDungeonData.ValidTilesets, List<RuntimeInjectionMetadata>>());
            bp.Clear();
            int missing = 0;
            for (uint n = r.VarU(); n > 0; n--)
            {
                var tileset = (GlobalDungeonData.ValidTilesets)r.I32();
                var list = new List<RuntimeInjectionMetadata>();
                for (uint k = r.VarU(); k > 0; k--)
                {
                    var data = InjectionByName(r.Str());
                    bool secret = r.Bool();
                    var meta = data ? new RuntimeInjectionMetadata(data) { forceSecret = secret } : null;
                    for (uint c = r.VarU(); c > 0; c--)
                    {
                        var mod = ReadModifier(ref r);
                        bool ok = r.Bool();
                        if (meta != null && mod != null) meta.SucceededRandomizationCheckMap[mod] = ok;
                    }
                    if (meta != null) list.Add(meta); else missing++;
                }
                bp[tileset] = list;
            }
            var used = MetaInjectionData.InjectionSetsUsedThisRun;
            used.Clear();
            for (uint n = r.VarU(); n > 0; n--)
            {
                var mod = ReadModifier(ref r);
                if (mod != null) used.Add(mod); else missing++;
            }
            if (missing > 0) Plugin.Log.LogWarning($"GenContext: {missing} run plan entries not found here");
        }

        static void ReadCounts(ref WireReader r, Dictionary<GlobalDungeonData.ValidTilesets, int> d)
        {
            d.Clear();
            for (uint n = r.VarU(); n > 0; n--) { var k = (GlobalDungeonData.ValidTilesets)r.I32(); d[k] = r.I32(); }
        }

        // ---------------- Naming injection data the same way on every PC ----------------

        static readonly Dictionary<string, SharedInjectionData> byName = new Dictionary<string, SharedInjectionData>();
        static readonly Dictionary<ProceduralFlowModifierData, KeyValuePair<string, int>> modifierIds = new Dictionary<ProceduralFlowModifierData, KeyValuePair<string, int>>();

        static void Index()
        {
            byName.Clear(); modifierIds.Clear();
            foreach (var s in Resources.FindObjectsOfTypeAll<SharedInjectionData>())
            {
                if (!s || byName.ContainsKey(s.name)) continue;
                byName[s.name] = s;
                if (s.InjectionData == null) continue;
                for (int i = 0; i < s.InjectionData.Count; i++)
                    if (s.InjectionData[i] != null && !modifierIds.ContainsKey(s.InjectionData[i]))
                        modifierIds[s.InjectionData[i]] = new KeyValuePair<string, int>(s.name, i);
            }
        }

        static SharedInjectionData InjectionByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!byName.TryGetValue(name, out var s) || !s) { Index(); byName.TryGetValue(name, out s); }
            return s;
        }

        static void WriteModifier(WireWriter w, ProceduralFlowModifierData m)
        {
            if (m == null || !modifierIds.ContainsKey(m)) Index();
            if (m != null && modifierIds.TryGetValue(m, out var id)) { w.Str(id.Key); w.VarU((uint)id.Value); }
            else { w.Str(""); w.VarU(0); }
        }

        static ProceduralFlowModifierData ReadModifier(ref WireReader r)
        {
            var s = InjectionByName(r.Str());
            int i = (int)r.VarU();
            return s && s.InjectionData != null && i < s.InjectionData.Count ? s.InjectionData[i] : null;
        }
    }

    /// <summary>GZip with a plain fallback: a PC where compression fails still sends and reads the data.</summary>
    internal static class Compression
    {
        public static byte[] Pack(byte[] raw)
        {
            try
            {
                using (var ms = new System.IO.MemoryStream())
                {
                    using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress))
                        gz.Write(raw, 0, raw.Length);
                    var packed = ms.ToArray();
                    return packed.Length < raw.Length ? packed : raw;
                }
            }
            catch (System.Exception) { return raw; }
        }

        public static byte[] Unpack(byte[] packed)
        {
            using (var ms = new System.IO.MemoryStream(packed))
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
            using (var o = new System.IO.MemoryStream())
            {
                var buf = new byte[16384];
                int n;
                while ((n = gz.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
                return o.ToArray();
            }
        }
    }
}
