using System;
using System.Net;
using System.Net.Sockets;

namespace GungeonOnline.Net
{
    /// <summary>
    /// Minimal STUN (RFC 5389) binding request, sent from the game's own UDP socket so the answer is the
    /// public address other players must use. Replies arrive through <see cref="NetNode.RawHandler"/>.
    /// </summary>
    public sealed class StunClient
    {
        const uint MagicCookie = 0x2112A442;
        readonly byte[] txId = new byte[12];
        readonly IDatagramSocket socket;
        readonly string server;
        readonly int serverPort;
        EndPoint serverEp;
        double nextSend;
        int attempts;

        public IPEndPoint Result { get; private set; }
        public bool Failed { get; private set; }
        public bool Done => Result != null || Failed;

        public StunClient(IDatagramSocket socket, string server = "stun.l.google.com", int port = 19302)
        {
            this.socket = socket; this.server = server; serverPort = port;
            new Random().NextBytes(txId);
        }

        public void Update(double now)
        {
            if (Done || now < nextSend) return;
            if (attempts++ >= 6) { Failed = true; return; }
            nextSend = now + 0.5;
            if (serverEp == null)
            {
                try
                {
                    foreach (var a in Dns.GetHostAddresses(server))
                        if (a.AddressFamily == AddressFamily.InterNetwork) { serverEp = new IPEndPoint(a, serverPort); break; }
                }
                catch { }
                if (serverEp == null) { Failed = true; return; }
            }
            var w = new WireWriter(20);
            w.U8(0x00); w.U8(0x01);  // Binding Request (big-endian type)
            w.U8(0x00); w.U8(0x00);  // length
            w.U8(0x21); w.U8(0x12); w.U8(0xA4); w.U8(0x42);
            w.Bytes(txId, 0, 12);
            socket.SendTo(serverEp, w.Buffer, w.Length);
        }

        /// <summary>Returns true if the datagram was our STUN reply.</summary>
        public bool TryHandle(byte[] d)
        {
            if (Done || d.Length < 20 || d[0] != 0x01 || d[1] != 0x01) return false;
            if (BE32(d, 4) != MagicCookie) return false;
            for (int i = 0; i < 12; i++) if (d[8 + i] != txId[i]) return false;
            int len = BE16(d, 2);
            int p = 20, end = Math.Min(d.Length, 20 + len);
            while (p + 4 <= end)
            {
                int type = BE16(d, p), alen = BE16(d, p + 2);
                int v = p + 4;
                if ((type == 0x0020 || type == 0x0001) && alen >= 8 && d[v + 1] == 0x01)
                {
                    int port = BE16(d, v + 2);
                    uint ip = BE32(d, v + 4);
                    if (type == 0x0020) { port ^= (int)(MagicCookie >> 16); ip ^= MagicCookie; }
                    Result = new IPEndPoint(new IPAddress(new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip }), port);
                    if (type == 0x0020) return true;
                }
                p = v + ((alen + 3) & ~3);
            }
            return Result != null;
        }

        static int BE16(byte[] d, int o) => d[o] << 8 | d[o + 1];
        static uint BE32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    }

    /// <summary>
    /// Invite codes: an IPv4 address and port packed into 12 Crockford base32 characters with a checksum,
    /// shown as XXXX-XXXX-XXXX. Raw "ip:port" text is accepted too (LAN, VPNs like Tailscale or Radmin).
    /// </summary>
    public static class InviteCode
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string Encode(IPEndPoint ep)
        {
            var b = ep.Address.GetAddressBytes();
            if (b.Length != 4) throw new ArgumentException("IPv4 only");
            ulong v = (ulong)b[0] << 40 | (ulong)b[1] << 32 | (ulong)b[2] << 24 | (ulong)b[3] << 16 | (ulong)ep.Port;
            v = v << 12 | Check(v);
            var c = new char[12];
            for (int i = 11; i >= 0; i--) { c[i] = Alphabet[(int)(v & 31)]; v >>= 5; }
            var s = new string(c);
            return s.Substring(0, 4) + "-" + s.Substring(4, 4) + "-" + s.Substring(8, 4);
        }

        public static bool TryParse(string text, out IPEndPoint ep)
        {
            ep = null;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();
            int colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                IPAddress ip;
                int port;
                if (IPAddress.TryParse(text.Substring(0, colon), out ip) && int.TryParse(text.Substring(colon + 1), out port) && port > 0 && port < 65536)
                { ep = new IPEndPoint(ip, port); return true; }
            }
            var s = text.ToUpperInvariant().Replace("-", "").Replace(" ", "").Replace('O', '0').Replace('I', '1').Replace('L', '1');
            if (s.Length != 12) return false;
            ulong v = 0;
            foreach (var ch in s)
            {
                int d = Alphabet.IndexOf(ch);
                if (d < 0) return false;
                v = v << 5 | (uint)d;
            }
            ulong data = v >> 12;
            if ((v & 0xFFF) != Check(data)) return false;
            var addr = new IPAddress(new[] { (byte)(data >> 40), (byte)(data >> 32), (byte)(data >> 24), (byte)(data >> 16) });
            ep = new IPEndPoint(addr, (int)(data & 0xFFFF));
            return true;
        }

        static ulong Check(ulong v)
        {
            ulong h = 0xCBF29CE484222325;
            for (int i = 0; i < 6; i++) { h ^= (v >> (8 * i)) & 0xFF; h *= 0x100000001B3; }
            return h & 0xFFF;
        }
    }

}
