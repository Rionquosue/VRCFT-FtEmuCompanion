using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Sits between VRCFaceTracking and VRChat: VRCFaceTracking's OSC output port is set to inPort, and every packet that
    /// arrives there from this computer is passed on unchanged to VRChat on outPort for as long as the proxy runs. While a
    /// writer is attached (SetWriter), each packet is also recorded as it passes.
    /// </summary>
    internal sealed class FTRecorderProxy : IDisposable
    {
        private readonly UdpClient receiver;
        private readonly UdpClient sender;
        private readonly IPEndPoint target;
        private readonly object writerGate = new object();
        private FTRecorderRecordingWriter writer;
        private readonly Thread thread;
        private volatile bool stopping;

        internal long Packets;
        internal readonly int InPort;
        internal readonly int OutPort;

        /// <summary>Starts (a writer) or stops (null) recording the packets; they are passed on either way.</summary>
        internal void SetWriter(FTRecorderRecordingWriter value)
        {
            // Under the lock, so a packet is never written to a writer that is being closed.
            lock (writerGate) writer = value;
        }

        internal FTRecorderProxy(int inPort, int outPort)
        {
            if (inPort == outPort) throw new ArgumentException("The proxy's in and out ports must differ.");
            InPort = inPort;
            OutPort = outPort;
            target = new IPEndPoint(IPAddress.Loopback, outPort);
            try
            {
                receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, inPort));
            }
            catch (SocketException exception)
            {
                throw new InvalidOperationException("UDP port " + inPort + " is in use (" + exception.SocketErrorCode + ").", exception);
            }
            sender = new UdpClient(AddressFamily.InterNetwork);
            thread = new Thread(Loop) { IsBackground = true, Name = "RiryTools FTRecorder proxy" };
            thread.Start();
        }

        private void Loop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (!stopping)
            {
                byte[] packet;
                try
                {
                    packet = receiver.Receive(ref remote);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
                {
                    if (!stopping) Console.Error.WriteLine("Proxy stopped: " + exception.Message);
                    return;
                }
                if (!IPAddress.IsLoopback(remote.Address)) continue;
                try
                {
                    sender.Send(packet, packet.Length, target);
                }
                catch (SocketException)
                {
                    // VRChat not running: the packet is still recorded.
                }
                lock (writerGate) writer?.WriteOsc(writer.Now, packet, packet.Length);
                Interlocked.Increment(ref Packets);
            }
        }

        public void Dispose()
        {
            stopping = true;
            receiver.Close();
            thread.Join(2000);
            sender.Dispose();
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder proxy
// ============================================================================
//
// FTRecorderProxy (this file)  127.0.0.1:inPort to 127.0.0.1:outPort, unchanged, for as long as it runs; loopback
//                            senders only. Packets counts them. SetWriter(writer) records each as an Osc entry until
//                            SetWriter(null). InPort, OutPort.
