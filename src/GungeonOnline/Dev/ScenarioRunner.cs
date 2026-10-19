using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GungeonOnline.Input;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Harness only: plays a scripted input file into player 0 through a virtual device, the same path
    /// network input will take. Line format (times in seconds from player spawn):
    ///   at duration moveX moveY aimX aimY [Button,Button...]
    ///   shot at name          (screenshot)
    ///   mark at name          (logs player position)
    /// </summary>
    internal static class ScenarioRunner
    {
        struct Step { public float At, Dur; public InputFrame Frame; }
        struct Marker { public float At; public string Kind, Name; }

        static readonly List<Step> steps = new List<Step>();
        static readonly List<Marker> markers = new List<Marker>();
        static float t0 = -1f;
        static NetInputDevice device;

        static bool bot, passive;
        static float botPhase;

        public static void Begin()
        {
            passive = System.Environment.GetEnvironmentVariable("GO_BOT") == "passive";
            if (System.Environment.GetEnvironmentVariable("GO_BOT") == "fight" || passive)
            {
                bot = true;
                device = InputPump.Route(0);
                InputPump.BeforePoll += Tick;
                t0 = Time.realtimeSinceStartup;
                Plugin.Log.LogInfo("Scenario: fight bot");
                return;
            }
            if (DevEnv.Script == null) return;
            if (!File.Exists(DevEnv.Script)) { Plugin.Log.LogWarning("Scenario missing: " + DevEnv.Script); return; }
            foreach (var raw in File.ReadAllLines(DevEnv.Script))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p[0] == "shot" || p[0] == "mark") { markers.Add(new Marker { Kind = p[0], At = F(p[1]), Name = p[2] }); continue; }
                var f = new InputFrame { Move = new Vector2(F(p[2]), F(p[3])), Aim = new Vector2(F(p[4]), F(p[5])) };
                if (p.Length > 6)
                    foreach (var b in p[6].Split(','))
                        f.Set((NetButton)Enum.Parse(typeof(NetButton), b), true);
                steps.Add(new Step { At = F(p[0]), Dur = F(p[1]), Frame = f });
            }
            device = InputPump.Route(0);
            InputPump.BeforePoll += Tick;
            t0 = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo($"Scenario: {steps.Count} steps, {markers.Count} markers");
        }

        /// <summary>Test bot: aim at the nearest living enemy in the same room, fire in bursts, strafe around it.</summary>
        static InputFrame BotFrame(float t)
        {
            var f = new InputFrame();
            var me = GameManager.Instance.PrimaryPlayer;
            // In the Breach, interacting would swap characters at the statues; the bot only plays dungeon floors.
            if (me == null || Game.SceneObjects.InBreach) return f;
            AIActor best = null;
            float bestD = 1e9f;
            foreach (var e in StaticReferenceManager.AllEnemies)
            {
                if (!e || e.healthHaver == null || e.healthHaver.IsDead || e.CompanionOwner != null) continue;
                if (e.ParentRoom != me.CurrentRoom) continue;
                float d = (e.CenterPosition - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; best = e; }
            }
            if (best == null) return Loot(me, t);
            var to = (best.CenterPosition - me.CenterPosition).normalized;
            f.Aim = to;
            botPhase += Time.deltaTime;
            if (!passive) f.Set(NetButton.Shoot, (botPhase % 0.3f) < 0.2f);
            var strafe = new Vector2(-to.y, to.x) * (Mathf.Sin(t * 0.7f) > 0 ? 1f : -1f);
            float dist = Mathf.Sqrt(bestD);
            f.Move = (strafe + (dist > 6f ? to : dist < 3f ? -to : Vector2.zero)).normalized;
            if (Mathf.Repeat(t, 4f) < 0.02f) f.Set(NetButton.DodgeRoll, true);
            return f;
        }

        /// <summary>Room clear: walk to the nearest item or closed chest in this room and interact.</summary>
        static InputFrame Loot(PlayerController me, float t)
        {
            var f = new InputFrame();
            Vector2? target = null;
            float bestD = 1e9f;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PickupObject>())
            {
                if (!p || p.transform.parent != null || !p.gameObject.activeInHierarchy || p.IsBeingSold) continue;
                Vector2 pos = p.transform.position;
                if (pos.GetAbsoluteRoom() != me.CurrentRoom) continue;
                float d = (pos - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; target = pos + new Vector2(0.5f, 0.5f); }
            }
            foreach (var ch in StaticReferenceManager.AllChests)
            {
                if (!ch || ch.IsOpen || ch.IsBroken) continue;
                Vector2 pos = ch.transform.position;
                if (pos.GetAbsoluteRoom() != me.CurrentRoom) continue;
                float d = (pos - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; target = pos + new Vector2(1f, 0.5f); }
            }
            foreach (var si in UnityEngine.Object.FindObjectsOfType<ShopItemController>())
            {
                if (!si || !si.item || !si.gameObject.activeInHierarchy) continue;
                Vector2 pos = si.transform.position;
                if (pos.GetAbsoluteRoom() != me.CurrentRoom) continue;
                float d = (pos - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; target = pos + new Vector2(0.5f, 0.5f); }
            }
            if (target == null) return f;
            var delta = target.Value - me.CenterPosition;
            if (delta.magnitude > 1.0f) f.Move = delta.normalized;
            f.Set(NetButton.Interact, Mathf.Repeat(t, 0.5f) < 0.1f);
            return f;
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        static void Tick()
        {
            if (t0 < 0) return;
            InputPump.EnforceRouting();
            float t = Time.realtimeSinceStartup - t0;
            var frame = default(InputFrame);
            if (bot) frame = BotFrame(t);
            else
                foreach (var s in steps)
                    if (t >= s.At && t < s.At + s.Dur) frame = s.Frame;
            device.Frame = frame;

            for (int i = markers.Count - 1; i >= 0; i--)
            {
                if (t < markers[i].At) continue;
                var m = markers[i];
                markers.RemoveAt(i);
                var p = GameManager.Instance.PrimaryPlayer;
                string pos = p != null ? p.CenterPosition.ToString("F2") : "none";
                Plugin.Log.LogInfo($"MARK {m.Name} t={t:F2} pos={pos}");
                if (m.Kind == "shot")
                {
                    var dir = Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
                    ScreenCapture.CaptureScreenshot(Path.Combine(dir, m.Name + ".png"));
                }
            }
        }
    }
}
