using System;
using System.Collections.Generic;
using System.Text;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>One OSC argument, kept in its own type.</summary>
    internal struct FTRecorderOscArgument
    {
        /// <summary>The type tag: f, i, T, F, s or b.</summary>
        internal char Tag;
        internal float Float;
        internal int Int;
        internal string Text;
        internal byte[] Blob;
    }

    internal sealed class FTRecorderOscMessage
    {
        internal string Address;
        internal readonly List<FTRecorderOscArgument> Arguments = new List<FTRecorderOscArgument>();
    }

    /// <summary>
    /// Reads and writes the OSC 1.0 packets FTRecorder deals with (the same code as FTEmulator's FTEmulatorOscCodec): messages and bundles carrying floats, ints, bools,
    /// strings and blobs. A packet that is not exactly right is refused whole: every length, the padding to four bytes,
    /// the nesting of bundles and every float are checked before anything is given out, and nothing is read past the
    /// end of the packet. A bundle's time tag is not used: its messages apply when they arrive (a late time tag would
    /// need a queue; none is kept).
    /// </summary>
    internal static class FTRecorderOscCodec
    {
        internal const string ParameterPrefix = "/avatar/parameters/";
        internal const int MaxBundleDepth = 4;
        internal const int MaxMessagesPerPacket = 1024;
        internal const int MaxArguments = 64;
        internal const int MaxPacketLength = 65507;

        /// <summary>The packet's messages, in order. False, with the reason, when any part of it is not valid OSC.</summary>
        internal static bool TryDecode(byte[] data, int length, List<FTRecorderOscMessage> messages, out string error)
        {
            int before = messages.Count;
            error = null;
            if (data == null || length <= 0 || length > data.Length || length > MaxPacketLength) error = "empty or oversized packet";
            else if ((length & 3) != 0) error = "packet length is not a multiple of four";
            else Element(data, 0, length, 0, messages, ref error);
            if (error == null) return true;
            messages.RemoveRange(before, messages.Count - before);
            return false;
        }

        private static void Element(byte[] data, int start, int end, int depth, List<FTRecorderOscMessage> messages, ref string error)
        {
            if (end - start >= 16 && data[start] == '#')
            {
                int at = start;
                if (!Text(data, ref at, end, out string header) || header != "#bundle") { error = "malformed bundle header"; return; }
                if (depth >= MaxBundleDepth) { error = "bundles nested deeper than " + MaxBundleDepth; return; }
                at += 8; // the time tag
                if (at > end) { error = "bundle cut short"; return; }
                while (at < end)
                {
                    if (end - at < 4) { error = "bundle element size cut short"; return; }
                    int size = Int(data, at);
                    at += 4;
                    if (size <= 0 || (size & 3) != 0 || size > end - at) { error = "bundle element size out of range"; return; }
                    Element(data, at, at + size, depth + 1, messages, ref error);
                    if (error != null) return;
                    at += size;
                }
                return;
            }
            Message(data, start, end, messages, ref error);
        }

        private static void Message(byte[] data, int start, int end, List<FTRecorderOscMessage> messages, ref string error)
        {
            if (messages.Count >= MaxMessagesPerPacket) { error = "more than " + MaxMessagesPerPacket + " messages in one packet"; return; }
            int at = start;
            if (!Text(data, ref at, end, out string address) || address.Length == 0 || address[0] != '/') { error = "malformed address"; return; }
            if (!Text(data, ref at, end, out string tags) || tags.Length == 0 || tags[0] != ',') { error = "malformed type tags"; return; }
            if (tags.Length - 1 > MaxArguments) { error = "more than " + MaxArguments + " arguments"; return; }
            var message = new FTRecorderOscMessage { Address = address };
            for (int i = 1; i < tags.Length; i++)
            {
                var argument = new FTRecorderOscArgument { Tag = tags[i] };
                switch (tags[i])
                {
                    case 'f':
                        if (end - at < 4) { error = "float cut short"; return; }
                        argument.Float = BitConverter.ToSingle(Reversed(data, at), 0);
                        if (float.IsNaN(argument.Float) || float.IsInfinity(argument.Float)) { error = "float is not finite"; return; }
                        at += 4;
                        break;
                    case 'i':
                        if (end - at < 4) { error = "int cut short"; return; }
                        argument.Int = Int(data, at);
                        at += 4;
                        break;
                    case 'T':
                    case 'F':
                        break;
                    case 's':
                        if (!Text(data, ref at, end, out argument.Text)) { error = "string cut short"; return; }
                        break;
                    case 'b':
                    {
                        if (end - at < 4) { error = "blob size cut short"; return; }
                        int size = Int(data, at);
                        at += 4;
                        int padded = size + 3 & ~3;
                        if (size < 0 || size > end - at || padded > end - at) { error = "blob size out of range"; return; }
                        argument.Blob = new byte[size];
                        Buffer.BlockCopy(data, at, argument.Blob, 0, size);
                        at += padded;
                        break;
                    }
                    default:
                        error = "unsupported type tag '" + tags[i] + "'";
                        return;
                }
                message.Arguments.Add(argument);
            }
            if (at != end) { error = "bytes left over after the arguments"; return; }
            messages.Add(message);
        }

        // An OSC string: ASCII, ended by a zero, padded with zeros to four bytes.
        private static bool Text(byte[] data, ref int at, int end, out string text)
        {
            text = null;
            int stop = at;
            while (stop < end && data[stop] != 0) stop++;
            if (stop >= end) return false;
            int next = at + (stop - at + 4 & ~3);
            if (next > end) return false;
            for (int i = stop; i < next; i++)
            {
                if (data[i] != 0) return false;
            }
            text = Encoding.UTF8.GetString(data, at, stop - at);
            at = next;
            return true;
        }

        private static int Int(byte[] data, int at)
        {
            return data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3];
        }

        private static byte[] Reversed(byte[] data, int at)
        {
            return BitConverter.IsLittleEndian ? new[] { data[at + 3], data[at + 2], data[at + 1], data[at] } : new[] { data[at], data[at + 1], data[at + 2], data[at + 3] };
        }

        /// <summary>One message with one argument: a string, bool, float, int or byte array.</summary>
        internal static byte[] Encode(string address, object argument)
        {
            var bytes = new List<byte>(64);
            Write(bytes, address);
            switch (argument)
            {
                case string text:
                    Write(bytes, ",s");
                    Write(bytes, text);
                    break;
                case bool flag:
                    Write(bytes, flag ? ",T" : ",F");
                    break;
                case float number:
                {
                    Write(bytes, ",f");
                    byte[] raw = BitConverter.GetBytes(number);
                    if (BitConverter.IsLittleEndian) Array.Reverse(raw);
                    bytes.AddRange(raw);
                    break;
                }
                case int whole:
                    Write(bytes, ",i");
                    WriteInt(bytes, whole);
                    break;
                case byte[] blob:
                    Write(bytes, ",b");
                    WriteInt(bytes, blob.Length);
                    bytes.AddRange(blob);
                    while ((bytes.Count & 3) != 0) bytes.Add(0);
                    break;
                default: throw new ArgumentException("Unsupported OSC argument.", nameof(argument));
            }
            return bytes.ToArray();
        }

        private static void Write(List<byte> bytes, string text)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(text));
            bytes.Add(0);
            while ((bytes.Count & 3) != 0) bytes.Add(0);
        }

        private static void WriteInt(List<byte> bytes, int value)
        {
            bytes.Add((byte)(value >> 24));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder OSC codec
// ============================================================================
//
// FTRecorderOscCodec (this file)
//   TryDecode(data, length, messages, out error)  A packet's messages (a bundle's in order). All or nothing: any fault
//                            refuses the packet. Checked: length a multiple of four and at most MaxPacketLength (65507),
//                            string endings and zero padding, type tags f i T F s b only, float finite, blob and bundle
//                            element sizes inside the packet, bundles at most MaxBundleDepth (4) deep, at most
//                            MaxMessagesPerPacket (1024) messages and MaxArguments (64) arguments, no bytes left over.
//   Encode(address, argument)  A message with one string, bool, float, int or blob.
//   ParameterPrefix          "/avatar/parameters/".
// FTRecorderOscMessage       Address and Arguments. FTRecorderOscArgument: Tag, Float, Int, Text, Blob.
