using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The Babble app's OSC output, received on 127.0.0.1 (one float per address, the address ending in the
    /// expression's name, such as /mouthFunnel, after any prefix the app adds). Only loopback senders are read. Takes the
    /// Babble module's place: the two cannot both listen on the same port.
    /// </summary>
    internal sealed class FTRecorderBabbleOscSource : FTRecorderSource
    {
        private readonly int port;
        private volatile UdpClient socket;

        internal FTRecorderBabbleOscSource(int port)
        {
            this.port = port;
        }

        internal override bool ProvidesEyes => false;

        protected override void Run()
        {
            try
            {
                socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException exception)
            {
                Status = "UDP port " + port + " could not be opened (" + exception.SocketErrorCode + "): is the Babble module running?";
                return;
            }
            Status = "Listening for the Babble app on 127.0.0.1:" + port;
            var unified = new float[FTRecorderExpressionMap.UnifiedNames.Length];
            var messages = new List<FTRecorderOscMessage>();
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (!Stopping)
            {
                byte[] packet;
                try
                {
                    packet = socket.Receive(ref remote);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
                {
                    if (!Stopping) Status = "Stopped listening: " + exception.Message;
                    return;
                }
                if (!IPAddress.IsLoopback(remote.Address)) continue;
                messages.Clear();
                if (!FTRecorderOscCodec.TryDecode(packet, packet.Length, messages, out _)) continue;
                bool changed = false;
                foreach (FTRecorderOscMessage message in messages)
                {
                    if (message.Arguments.Count != 1 || message.Arguments[0].Tag != 'f') continue;
                    string name = message.Address.Substring(message.Address.LastIndexOf('/') + 1);
                    changed |= FTRecorderExpressionMap.Set(name, message.Arguments[0].Float, unified);
                }
                if (changed) Publish(unified, null);
            }
        }

        protected override void Interrupt()
        {
            socket?.Close();
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder Babble OSC source
// ============================================================================
//
// FTRecorderBabbleOscSource (this file)  The Babble app's OSC on 127.0.0.1:babbleOscPort (8888); loopback senders only.
//                            Each single-float message's last address part names the expression (any case), set
//                            through FTRecorderExpressionMap. No eye data.
