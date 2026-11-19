using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Records the visemes VRChat makes from the microphone. VRChat sends avatar parameters to every OSCQuery service it
    /// finds that lists /avatar; this service lists /avatar/parameters/Viseme and Voice, so VRChat sends them here. It
    /// is announced over multicast DNS as "_oscjson._tcp" and "_osc._udp" at 127.0.0.1, answers HTTP on 127.0.0.1 only,
    /// and keeps only Viseme and Voice from loopback senders.
    /// </summary>
    internal sealed class FTRecorderVrchatListener : IDisposable
    {
        private const string JsonService = "_oscjson._tcp.local";
        private const string OscService = "_osc._udp.local";
        private const int MdnsPort = 5353;
        private static readonly IPAddress MdnsAddress = IPAddress.Parse("224.0.0.251");
        private static readonly HashSet<string> Kept = new HashSet<string>(StringComparer.Ordinal) { "Viseme", "Voice" };

        private readonly Func<double> clock;
        private readonly Action<double, string, char, float> sink;
        private readonly string name = "RiryTools-FTRecorder-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        private readonly UdpClient osc;
        private readonly TcpListener http;
        private readonly List<UdpClient> senders = new List<UdpClient>();
        private readonly UdpClient mdns;
        private readonly List<Thread> threads = new List<Thread>();
        private readonly int oscPort, httpPort;
        private volatile bool stopping;

        internal long Received;

        /// <summary>False when no network interface could carry the announcement: VRChat cannot find the service.</summary>
        internal bool Discoverable => mdns != null && senders.Count > 0;

        /// <param name="clock">The time stamp each value gets, in seconds.</param>
        /// <param name="sink">Gets each kept value: time, name, type ('i', 'f' or 'b') and value.</param>
        internal FTRecorderVrchatListener(Func<double> clock, Action<double, string, char, float> sink)
        {
            this.clock = clock;
            this.sink = sink;
            osc = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            oscPort = ((IPEndPoint)osc.Client.LocalEndPoint).Port;
            http = new TcpListener(IPAddress.Loopback, 0);
            http.Start();
            httpPort = ((IPEndPoint)http.LocalEndpoint).Port;
            mdns = OpenMdns();
            Start(ReceiveOsc, "OSC");
            Start(ServeHttp, "OSCQuery");
            if (Discoverable) Start(Discovery, "discovery");
        }

        private void Start(ThreadStart work, string label)
        {
            var thread = new Thread(() =>
            {
                try { work(); }
                catch (Exception exception) { if (!stopping) Console.Error.WriteLine("VRChat listener (" + label + ") stopped: " + exception.Message); }
            })
            { IsBackground = true, Name = "RiryTools FTRecorder " + label };
            threads.Add(thread);
            thread.Start();
        }

        private void ReceiveOsc()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var messages = new List<FTRecorderOscMessage>();
            while (!stopping)
            {
                byte[] packet;
                try
                {
                    packet = osc.Receive(ref remote);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
                {
                    return;
                }
                if (!IPAddress.IsLoopback(remote.Address)) continue;
                messages.Clear();
                if (!FTRecorderOscCodec.TryDecode(packet, packet.Length, messages, out _)) continue;
                double now = clock();
                foreach (FTRecorderOscMessage message in messages)
                {
                    if (!message.Address.StartsWith(FTRecorderOscCodec.ParameterPrefix, StringComparison.Ordinal) || message.Arguments.Count != 1) continue;
                    string parameter = message.Address.Substring(FTRecorderOscCodec.ParameterPrefix.Length);
                    if (!Kept.Contains(parameter)) continue;
                    FTRecorderOscArgument argument = message.Arguments[0];
                    switch (argument.Tag)
                    {
                        case 'i': sink(now, parameter, 'i', argument.Int); break;
                        case 'f': sink(now, parameter, 'f', argument.Float); break;
                        case 'T': sink(now, parameter, 'b', 1f); break;
                        case 'F': sink(now, parameter, 'b', 0f); break;
                        default: continue;
                    }
                    Interlocked.Increment(ref Received);
                }
            }
        }

        // A minimal HTTP server: one GET per connection, JSON back, closed after. Requests over 8 KB or slower than two
        // seconds are dropped.
        private void ServeHttp()
        {
            while (!stopping)
            {
                TcpClient client;
                try
                {
                    client = http.AcceptTcpClient();
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException || exception is InvalidOperationException)
                {
                    return;
                }
                using (client)
                {
                    try
                    {
                        client.ReceiveTimeout = 2000;
                        client.SendTimeout = 2000;
                        NetworkStream stream = client.GetStream();
                        string request = ReadRequest(stream);
                        if (request == null) continue;
                        string[] parts = request.Split(' ');
                        int status = 200;
                        string body = parts.Length >= 2 && parts[0] == "GET" ? Respond(parts[1], out status) : Error(405, out status);
                        byte[] content = Encoding.UTF8.GetBytes(body);
                        string head = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Error") + "\r\nContent-Type: application/json\r\nContent-Length: " + content.Length + "\r\nConnection: close\r\n\r\n";
                        byte[] header = Encoding.ASCII.GetBytes(head);
                        stream.Write(header, 0, header.Length);
                        stream.Write(content, 0, content.Length);
                    }
                    catch (Exception exception) when (exception is IOException || exception is SocketException || exception is ObjectDisposedException)
                    {
                        // The asker went away.
                    }
                }
            }
        }

        private static string ReadRequest(NetworkStream stream)
        {
            var buffer = new byte[8192];
            int filled = 0;
            while (filled < buffer.Length)
            {
                int read = stream.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0) return null;
                filled += read;
                string text = Encoding.ASCII.GetString(buffer, 0, filled);
                int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0) return text.Substring(0, text.IndexOf("\r\n", StringComparison.Ordinal));
            }
            return null;
        }

        private static string Error(int code, out int status)
        {
            status = code;
            return "{}";
        }

        private string Respond(string target, out int status)
        {
            status = 200;
            int mark = target.IndexOf('?');
            string path = mark >= 0 ? target.Substring(0, mark) : target;
            string query = mark >= 0 ? target.Substring(mark + 1) : "";
            if (query.Contains("HOST_INFO", StringComparison.Ordinal))
            {
                return Json(new Dictionary<string, object>
                {
                    { "NAME", name },
                    { "EXTENSIONS", new Dictionary<string, object> { { "ACCESS", true }, { "CLIPMODE", false }, { "RANGE", true }, { "TYPE", true }, { "VALUE", true } } },
                    { "OSC_IP", "127.0.0.1" },
                    { "OSC_PORT", oscPort },
                    { "OSC_TRANSPORT", "UDP" },
                });
            }
            Dictionary<string, object> node = Find(Tree(), path.TrimEnd('/'));
            if (node == null) return Error(404, out status);
            return Json(node);
        }

        // The tree: /avatar/change, /avatar/parameters/Viseme (int) and /avatar/parameters/Voice (float), all writable.
        private static Dictionary<string, object> Tree()
        {
            Dictionary<string, object> Leaf(string path, string type) => new Dictionary<string, object> { { "FULL_PATH", path }, { "ACCESS", 2 }, { "TYPE", type } };
            Dictionary<string, object> Branch(string path, Dictionary<string, object> contents) => new Dictionary<string, object> { { "FULL_PATH", path }, { "ACCESS", 0 }, { "CONTENTS", contents } };
            var parameters = Branch("/avatar/parameters", new Dictionary<string, object>
            {
                { "Viseme", Leaf("/avatar/parameters/Viseme", "i") },
                { "Voice", Leaf("/avatar/parameters/Voice", "f") },
            });
            var avatar = Branch("/avatar", new Dictionary<string, object> { { "change", Leaf("/avatar/change", "s") }, { "parameters", parameters } });
            return Branch("/", new Dictionary<string, object> { { "avatar", avatar } });
        }

        private static Dictionary<string, object> Find(Dictionary<string, object> node, string path)
        {
            if (path.Length == 0 || path == "/") return node;
            foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.TryGetValue("CONTENTS", out object contents) || !((Dictionary<string, object>)contents).TryGetValue(part, out object child)) return null;
                node = (Dictionary<string, object>)child;
            }
            return node;
        }

        private static string Json(object value) => JsonSerializer.Serialize(value);

        private UdpClient OpenMdns()
        {
            var addresses = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up || !network.SupportsMulticast) continue;
                    foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        addresses.Add(address.Address);
                        break;
                    }
                }
            }
            catch (NetworkInformationException exception)
            {
                Console.Error.WriteLine("Network interfaces could not be listed: " + exception.Message);
            }
            try
            {
                var receiver = new UdpClient(AddressFamily.InterNetwork);
                receiver.ExclusiveAddressUse = false;
                receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                foreach (IPAddress address in addresses)
                {
                    try
                    {
                        receiver.JoinMulticastGroup(MdnsAddress, address);
                        var sender = new UdpClient(AddressFamily.InterNetwork);
                        sender.ExclusiveAddressUse = false;
                        sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        sender.Client.Bind(new IPEndPoint(address, MdnsPort));
                        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                        senders.Add(sender);
                    }
                    catch (SocketException exception)
                    {
                        Console.Error.WriteLine("Multicast DNS on " + address + ": " + exception.SocketErrorCode);
                    }
                }
                return receiver;
            }
            catch (SocketException exception)
            {
                Console.Error.WriteLine("UDP port 5353 could not be shared (" + exception.SocketErrorCode + "): VRChat cannot find the listener.");
                return null;
            }
        }

        // Announces both services at start and every 15 seconds, and answers any question for either type at once.
        private void Discovery()
        {
            mdns.Client.ReceiveTimeout = 500;
            string host = name + ".local";
            byte[] json = FTRecorderDnsPacket.Announce(JsonService, name, host, httpPort, IPAddress.Loopback);
            byte[] udp = FTRecorderDnsPacket.Announce(OscService, name, host, oscPort, IPAddress.Loopback);
            var group = new IPEndPoint(MdnsAddress, MdnsPort);
            var remote = new IPEndPoint(IPAddress.Any, 0);
            DateTime next = DateTime.MinValue;
            while (!stopping)
            {
                if (DateTime.UtcNow >= next)
                {
                    Send(json, group);
                    Send(udp, group);
                    next = DateTime.UtcNow.AddSeconds(15);
                }
                byte[] data;
                try
                {
                    data = mdns.Receive(ref remote);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut || exception.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
                {
                    return;
                }
                if (!FTRecorderDnsPacket.TryParse(data, data.Length, out FTRecorderDnsPacket packet) || packet.Response) continue;
                foreach (FTRecorderDnsPacket.Question question in packet.Questions)
                {
                    if (question.Type != FTRecorderDnsPacket.TypePtr && question.Type != FTRecorderDnsPacket.TypeAny) continue;
                    string asked = question.Name.TrimEnd('.');
                    if (string.Equals(asked, JsonService, StringComparison.OrdinalIgnoreCase)) Send(json, group);
                    else if (string.Equals(asked, OscService, StringComparison.OrdinalIgnoreCase)) Send(udp, group);
                }
            }
        }

        private void Send(byte[] packet, IPEndPoint group)
        {
            foreach (UdpClient sender in senders)
            {
                try { sender.Send(packet, packet.Length, group); }
                catch (SocketException) { }
            }
        }

        public void Dispose()
        {
            stopping = true;
            osc.Close();
            http.Stop();
            mdns?.Close();
            foreach (Thread thread in threads) thread.Join(2000);
            foreach (UdpClient sender in senders) sender.Dispose();
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder VRChat listener
// ============================================================================
//
// FTRecorderVrchatListener (this file)  An OSCQuery service VRChat sends Viseme and Voice to.
//   Announced                "_oscjson._tcp.local" (HTTP port) and "_osc._udp.local" (OSC port) as
//                            RiryTools-FTRecorder-xxxxxx at 127.0.0.1; every 15 s and on any PTR or ANY question.
//   HTTP                     127.0.0.1 only; GET ?HOST_INFO (NAME, OSC_IP 127.0.0.1, OSC_PORT, UDP) or a tree node:
//                            /, /avatar, /avatar/change, /avatar/parameters, .../Viseme (i), .../Voice (f). 404 else.
//   OSC                      127.0.0.1, loopback senders; /avatar/parameters/Viseme and Voice handed to the sink with
//                            the clock's time; everything else dropped. Received counts them.
//   Discoverable             False when multicast DNS could not be set up.
