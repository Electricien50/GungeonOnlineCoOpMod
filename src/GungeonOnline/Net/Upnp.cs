using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GungeonOnline.Net
{
    /// <summary>
    /// Best-effort UPnP IGD port mapping so the host's router forwards the game port without manual setup.
    /// Runs on a background thread; the session never waits on it. Mapping is removed on shutdown.
    /// </summary>
    public sealed class UpnpMapper
    {
        public enum State { Idle, Working, Mapped, Failed }
        public volatile State Status = State.Idle;
        public string Error;
        public string ExternalIp;

        string controlUrl, serviceType;
        int mappedPort;

        public void MapAsync(int port, string description)
        {
            if (Status == State.Working) return;
            Status = State.Working;
            new Thread(() =>
            {
                try
                {
                    Discover();
                    string local = LocalIpFor(new Uri(controlUrl).Host);
                    Soap("AddPortMapping",
                        "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>UDP</NewProtocol>" +
                        "<NewInternalPort>" + port + "</NewInternalPort><NewInternalClient>" + local + "</NewInternalClient>" +
                        "<NewEnabled>1</NewEnabled><NewPortMappingDescription>" + description + "</NewPortMappingDescription>" +
                        "<NewLeaseDuration>0</NewLeaseDuration>");
                    var ip = Soap("GetExternalIPAddress", "");
                    var m = Regex.Match(ip, "<NewExternalIPAddress>([^<]+)</NewExternalIPAddress>");
                    if (m.Success) ExternalIp = m.Groups[1].Value;
                    mappedPort = port;
                    Status = State.Mapped;
                }
                catch (Exception e)
                {
                    Error = e.Message;
                    Status = State.Failed;
                }
            }) { IsBackground = true, Name = "GungeonOnline UPnP" }.Start();
        }

        public void Unmap()
        {
            if (Status != State.Mapped || mappedPort == 0) return;
            int port = mappedPort;
            mappedPort = 0;
            try
            {
                Soap("DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>UDP</NewProtocol>");
            }
            catch { }
        }

        void Discover()
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.ReceiveTimeout = 2500;
                string req = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n\r\n";
                var data = Encoding.ASCII.GetBytes(req);
                var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                s.SendTo(data, group);
                s.SendTo(data, group);
                var buf = new byte[4096];
                DateTime end = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < end)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref from); } catch (SocketException) { break; }
                    var text = Encoding.ASCII.GetString(buf, 0, n);
                    var loc = Regex.Match(text, @"(?im)^location:\s*(\S+)");
                    if (!loc.Success) continue;
                    if (TryDescription(loc.Groups[1].Value)) return;
                }
            }
            throw new Exception("no UPnP router answered");
        }

        bool TryDescription(string location)
        {
            string xml;
            try { xml = Http("GET", location, null, null); } catch { return false; }
            foreach (var st in new[] { "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANIPConnection:2", "urn:schemas-upnp-org:service:WANPPPConnection:1" })
            {
                int i = xml.IndexOf(st, StringComparison.Ordinal);
                if (i < 0) continue;
                var m = Regex.Match(xml.Substring(i), "<controlURL>([^<]+)</controlURL>");
                if (!m.Success) continue;
                controlUrl = new Uri(new Uri(location), m.Groups[1].Value).ToString();
                serviceType = st;
                return true;
            }
            return false;
        }

        string Soap(string action, string args)
        {
            string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                          "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action + " xmlns:u=\"" + serviceType + "\">" +
                          args + "</u:" + action + "></s:Body></s:Envelope>";
            return Http("POST", controlUrl, body, "\"" + serviceType + "#" + action + "\"");
        }

        static string Http(string method, string url, string body, string soapAction)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 3000;
            req.ReadWriteTimeout = 3000;
            if (body != null)
            {
                req.ContentType = "text/xml; charset=\"utf-8\"";
                if (soapAction != null) req.Headers.Add("SOAPACTION", soapAction);
                var b = Encoding.UTF8.GetBytes(body);
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream()))
                return r.ReadToEnd();
        }

        /// <summary>The local IPv4 address the OS would use to reach the router.</summary>
        static string LocalIpFor(string routerHost)
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Connect(Dns.GetHostAddresses(routerHost)[0], 1900);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
        }
    }
}
