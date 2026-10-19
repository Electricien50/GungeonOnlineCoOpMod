using GungeonOnline.Net;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>The Gungeon Online window (F8 by default): host, join, invite codes, connection health.</summary>
    internal sealed class Overlay : MonoBehaviour
    {
        bool open;
        Rect rect = new Rect(20, 20, 420, 10);
        string joinCode = "";
        string letInCode = "";

        void Update()
        {
            if (UnityEngine.Input.GetKeyDown(NetConfig.OverlayKey.Value)) open = !open;
        }

        void OnGUI()
        {
            var s = NetSession.Instance;
            if (s == null) return;
            if (!open)
            {
                if (NetSession.Active) GUI.Label(new Rect(8, Screen.height - 24, 600, 22), StatusLine(s));
                return;
            }
            rect = GUILayout.Window(0x60, rect, id => Draw(s), "Gungeon Online " + Plugin.Version);
        }

        static string StatusLine(NetSession s)
        {
            if (s.Node == null || s.Node.Connections.Count == 0) return "Online: " + s.Status;
            var sb = new System.Text.StringBuilder("Online:");
            foreach (var c in s.Node.Connections)
                sb.Append($"  P{c.Slot + 1} {c.RttMs:F0} ms {c.LossPercent:F0}% loss");
            return sb.ToString();
        }

        void Draw(NetSession s)
        {
            GUILayout.Label("Status: " + s.Status);
            if (!string.IsNullOrEmpty(s.LastError)) GUILayout.Label("Last problem: " + s.LastError);

            if (!NetSession.Active)
            {
                if (GUILayout.Button("Host a session")) { s.LastError = null; s.Host(); }
                GUILayout.Space(6);
                GUILayout.Label("Join with the host's invite code (or ip:port):");
                joinCode = GUILayout.TextField(joinCode, 40);
                if (GUILayout.Button("Join")) { s.LastError = null; s.Join(joinCode); }
            }
            else
            {
                string code = s.InviteCodeText ?? (s.Stun != null && s.Stun.Failed ? "unavailable (share ip:port instead)" : "finding your address...");
                GUILayout.Label((NetSession.IsHost ? "Your invite code: " : "Your reply code: ") + code);
                if (s.InviteCodeText != null && GUILayout.Button("Copy code")) GUIUtility.systemCopyBuffer = s.InviteCodeText;
                if (s.Upnp != null)
                    GUILayout.Label("Router port forwarding: " + s.Upnp.Status + (s.Upnp.Status == UpnpMapper.State.Failed ? " (" + s.Upnp.Error + ")" : ""));
                if (NetSession.IsHost)
                {
                    GUILayout.Label("If a friend cannot get in, paste their reply code here:");
                    letInCode = GUILayout.TextField(letInCode, 40);
                    if (GUILayout.Button("Let them in")) s.LetIn(letInCode);
                }
                GUILayout.Space(6);
                GUILayout.Label("Players:");
                GUILayout.Label($"  P{NetSession.LocalSlot + 1} (you) {NetConfig.PlayerName.Value}");
                if (s.Node != null)
                    foreach (var c in s.Node.Connections)
                        GUILayout.Label($"  P{c.Slot + 1} {c.Tag ?? "host"}  ping {c.RttMs:F0} ms  jitter {c.RttVarMs:F0} ms  loss {c.LossPercent:F1}%");
                if (GUILayout.Button("Leave session")) s.Leave("left");
            }
            GUI.DragWindow();
        }
    }
}
