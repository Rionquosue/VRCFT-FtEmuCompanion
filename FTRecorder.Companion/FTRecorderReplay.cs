using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Sends a recording back out over OSC at its recorded times: recorded packets unchanged, recorded parameters as
    /// /avatar/parameters messages, and visemes made from audio as Viseme (int) and Voice (float). Expression frames
    /// are for the module's playback source and are not sent. VRChat ignores Viseme and Voice sent to it (it makes its
    /// own); FTEmulator in Unity takes them.
    /// </summary>
    internal static class FTRecorderReplay
    {
        internal static void Run(FTRecorderRecording recording, int port, bool loop, CancellationToken cancel)
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            var target = new IPEndPoint(IPAddress.Loopback, port);
            var entries = recording.Entries.FindAll(entry => entry.Kind != FTRecorderEntryKind.Expressions);
            if (entries.Count == 0)
            {
                Console.WriteLine(recording.Entries.Count == 0
                    ? "Nothing to send: the recording is empty."
                    : "Nothing to send: the recording has only expression frames (play those with the module's playback source).");
                return;
            }
            entries.Sort((a, b) => a.Time.CompareTo(b.Time));
            double start = entries[0].Time;
            do
            {
                var clock = Stopwatch.StartNew();
                foreach (FTRecorderEntry entry in entries)
                {
                    double wait = entry.Time - start - clock.Elapsed.TotalSeconds;
                    if (wait > 0 && cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait))) return;
                    if (cancel.IsCancellationRequested) return;
                    switch (entry.Kind)
                    {
                        case FTRecorderEntryKind.Osc:
                            Send(socket, target, entry.Packet);
                            break;
                        case FTRecorderEntryKind.Parameter:
                            string address = FTRecorderOscCodec.ParameterPrefix + entry.Name;
                            object value = entry.Type == 'i' ? (object)(int)entry.Value : entry.Type == 'b' ? (object)(entry.Value != 0f) : entry.Value;
                            Send(socket, target, FTRecorderOscCodec.Encode(address, value));
                            break;
                        case FTRecorderEntryKind.Viseme:
                            Send(socket, target, FTRecorderOscCodec.Encode(FTRecorderOscCodec.ParameterPrefix + "Viseme", entry.Viseme));
                            Send(socket, target, FTRecorderOscCodec.Encode(FTRecorderOscCodec.ParameterPrefix + "Voice", entry.Voice));
                            break;
                    }
                }
            }
            while (loop && !cancel.IsCancellationRequested);
        }

        private static void Send(UdpClient socket, IPEndPoint target, byte[] packet)
        {
            try { socket.Send(packet, packet.Length, target); }
            catch (SocketException) { }
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder replay
// ============================================================================
//
// FTRecorderReplay (this file)
//   Run(recording, port, loop, cancel)  To 127.0.0.1:port at recorded times: Osc entries unchanged, Parameter entries
//                            as /avatar/parameters/<name>, Viseme entries as Viseme (i) and Voice (f). Expression
//                            frames are skipped.
