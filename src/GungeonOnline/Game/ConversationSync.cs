using System.Collections.Generic;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// A friend's conversation with an NPC (or shrine) also runs on this PC, so its outcome is the same here.
    /// The game keeps one answer-choice state for the whole screen and reads it from this PC's input: a friend's
    /// conversation took that state over, waited for button presses that never arrived and blocked our own
    /// conversations, which then never ended (no moving until death). It could also take our answer as theirs.
    /// Now a friend's conversation here pages on its own and answers with the choice their PC sends;
    /// the answer-choice state belongs to our own conversations only.
    /// </summary>
    internal sealed class ConversationSync : MonoBehaviour
    {
        const byte MsgTalk = 7; // Lobby channel
        const float PageSeconds = 1.0f, AnswerMaxAge = 120f;

        sealed class Answer { public string Key; public int Index; public float At; }
        sealed class Friend { public bool Asked; public readonly List<Answer> Answers = new List<Answer>(); }

        static readonly Dictionary<int, Friend> friends = new Dictionary<int, Friend>();
        static readonly Dictionary<DialogueBox, float> nextPage = new Dictionary<DialogueBox, float>();

        // Set while a DialogueBox runs its frame.
        static DialogueBox current;
        static PlayerController remoteTalker;
        static int localState; // 1: our own choice is showing, not yet sent

        static readonly AccessTools.FieldRef<DialogueBox, TalkDoerLite> talkDoerField = AccessTools.FieldRefAccess<DialogueBox, TalkDoerLite>("m_talkDoer");
        static readonly AccessTools.FieldRef<GameUIRoot, bool> displayingField = AccessTools.FieldRefAccess<GameUIRoot, bool>("m_displayingPlayerConversationOptions");

        public static int AnswersSent, AnswersUsed;
        public static bool LocalAnswered; // tests
        static bool syncOff;

        public static void Install(Harmony h)
        {
            syncOff = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_CONVSYNC") == "off"; // tests: the old behaviour
            var t = typeof(ConversationSync);
            h.Patch(AccessTools.Method(typeof(DialogueBox), nameof(DialogueBox.OnUpdate)),
                prefix: new HarmonyMethod(t, nameof(DialoguePrefix)), finalizer: new HarmonyMethod(t, nameof(DialogueFinalizer)));
            h.Patch(AccessTools.Method(typeof(BraveInput), nameof(BraveInput.WasAdvanceDialoguePressed), new[] { typeof(bool).MakeByRefType() }),
                prefix: new HarmonyMethod(t, nameof(AdvancePrefix)));
            h.Patch(AccessTools.Method(typeof(GameUIRoot), nameof(GameUIRoot.DisplayPlayerConversationOptions), new[] { typeof(PlayerController), typeof(string[]) }),
                prefix: new HarmonyMethod(t, nameof(DisplayPrefix)) { priority = Priority.First });
            h.Patch(AccessTools.Method(typeof(GameUIRoot), nameof(GameUIRoot.GetPlayerConversationResponse)),
                prefix: new HarmonyMethod(t, nameof(ResponsePrefix)), postfix: new HarmonyMethod(t, nameof(ResponsePostfix)));
        }

        void Start()
        {
            NetSession.Instance.On(Channel.Lobby, OnLobby);
            NetSession.Instance.SessionEnded += Clear;
        }

        static void Clear() { friends.Clear(); nextPage.Clear(); localState = 0; }

        static Friend For(int slot)
        {
            if (!friends.TryGetValue(slot, out var f)) friends[slot] = f = new Friend();
            return f;
        }

        static string KeyOf(DialogueBox box)
        {
            var td = box != null ? talkDoerField(box) : null;
            if (!td) return "";
            Vector2 p = td.transform.position;
            return td.name + "@" + Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.y);
        }

        // ---------------- Patches ----------------

        static void DialoguePrefix(DialogueBox __instance, out DialogueBox __state)
        {
            __state = current;
            current = __instance;
            var td = talkDoerField(__instance);
            remoteTalker = !syncOff && NetSession.Active && td && PlayerSlots.IsRemote(td.TalkingPlayer) ? td.TalkingPlayer : null;
        }

        static System.Exception DialogueFinalizer(System.Exception __exception, DialogueBox __state)
        {
            current = __state;
            remoteTalker = null;
            return __exception;
        }

        /// <summary>A friend's conversation here turns its pages by itself; their own PC follows their buttons.
        /// (Reading this PC's input for them also let our mouse clicks and Enter key page their text.)</summary>
        static bool AdvancePrefix(ref bool __result, ref bool suppressThisClick)
        {
            if (current != null && remoteTalker == null && AutoTalk)
            {
                // Tests: our own conversation pages itself too.
                var td = talkDoerField(current);
                if (!td || td.TalkingPlayer != PlayerSlots.Local) return true;
            }
            else if (remoteTalker == null || current == null) return true;
            suppressThisClick = false;
            float now = Time.realtimeSinceStartup;
            if (!nextPage.TryGetValue(current, out var at)) { nextPage[current] = now + PageSeconds; __result = false; return false; }
            __result = now >= at;
            if (__result) nextPage[current] = now + PageSeconds;
            return false;
        }

        static bool DisplayPrefix(PlayerController interactingPlayer, ref bool __result)
        {
            if (!NetSession.Active || interactingPlayer == null) return true;
            if (!syncOff && PlayerSlots.IsRemote(interactingPlayer))
            {
                // Their PC shows the choice; here we only wait for what they picked.
                For(PlayerSlots.SlotOf(interactingPlayer)).Asked = true;
                __result = true;
                return false;
            }
            if (interactingPlayer == PlayerSlots.Local) localState = 1;
            return true;
        }

        static bool ResponsePrefix(ref bool __result, ref int responseIndex)
        {
            if (!NetSession.Active || syncOff) return true;
            int slot = -1;
            string key = "";
            if (remoteTalker != null) { slot = PlayerSlots.SlotOf(remoteTalker); key = KeyOf(current); }
            else if (localState != 1 && current == null && GameUIRoot.HasInstance && !displayingField(GameUIRoot.Instance))
            {
                // A shrine or similar asking without a DialogueBox: a friend's open question, if any.
                foreach (var kv in friends) if (kv.Value.Asked) { slot = kv.Key; break; }
            }
            if (slot < 0) return true;
            var f = For(slot);
            float now = Time.realtimeSinceStartup;
            f.Answers.RemoveAll(a => now - a.At > AnswerMaxAge);
            int i = f.Answers.FindIndex(a => a.Key == key);
            if (i >= 0)
            {
                responseIndex = f.Answers[i].Index;
                f.Answers.RemoveAt(i);
                f.Asked = false;
                __result = true;
                AnswersUsed++;
                Plugin.Log.LogInfo($"ConversationSync: slot {slot} answered {responseIndex} ({(key.Length > 0 ? key : "shrine")})");
            }
            else { responseIndex = 0; __result = false; }
            return false;
        }

        // Tests (GO_AUTO_TALK=1): our own conversations page themselves and take the first answer.
        static readonly bool AutoTalk = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_AUTO_TALK") == "1";
        static float shownAt = -1f;

        void Update()
        {
            if (!AutoTalk || !GameUIRoot.HasInstance) return;
            if (localState == 1 && displayingField(GameUIRoot.Instance))
            {
                if (shownAt < 0f) shownAt = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - shownAt > 1.2f) { shownAt = -1f; GameUIRoot.Instance.SelectConversationResponse(); }
            }
            else shownAt = -1f;
        }

        static void ResponsePostfix(bool __result, int responseIndex)
        {
            if (!NetSession.Active || remoteTalker != null || !__result || localState != 1) return;
            localState = 0;
            LocalAnswered = true;
            if (syncOff) return;
            Send(NetSession.LocalSlot, current != null ? KeyOf(current) : "", responseIndex, null);
        }

        // ---------------- Network ----------------

        static byte[] Msg(int slot, string key, int index)
        {
            var w = new WireWriter(64);
            w.U8(MsgTalk); w.U8((byte)slot); w.Str(key); w.U8((byte)index);
            return w.ToArray();
        }

        static void Send(int slot, string key, int index, Connection except)
        {
            var data = Msg(slot, key, index);
            foreach (var c in NetSession.Instance.Node.Connections)
                if (c != except) c.Send((byte)Channel.Lobby, data, Delivery.ReliableOrdered);
            if (except == null) { AnswersSent++; Plugin.Log.LogInfo($"ConversationSync: sent answer {index} ({(key.Length > 0 ? key : "shrine")})"); }
        }

        void OnLobby(Connection c, byte[] data, Delivery d)
        {
            if (data.Length == 0 || data[0] != MsgTalk) return;
            var r = new WireReader(data, 1, data.Length - 1);
            int slot = r.U8();
            string key = r.Str();
            int index = r.U8();
            if (NetSession.IsHost) { slot = c.Slot; Send(slot, key, index, c); }
            if (slot == NetSession.LocalSlot) return;
            var f = For(slot);
            f.Answers.Add(new Answer { Key = key, Index = index, At = Time.realtimeSinceStartup });
            if (f.Answers.Count > 8) f.Answers.RemoveAt(0);
        }
    }
}
