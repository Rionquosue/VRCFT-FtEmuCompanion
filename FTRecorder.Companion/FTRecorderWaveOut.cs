using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Plays mono 16-bit audio to one Windows playback device, chosen by name, through the Windows multimedia API
    /// (winmm). Used to feed a virtual audio cable that VRChat takes as its microphone.
    /// </summary>
    internal static class FTRecorderWaveOut
    {
        private const int Success = 0;
        private const uint Done = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Caps
        {
            internal ushort Manufacturer, Product;
            internal uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
            internal uint Formats;
            internal ushort Channels, Reserved;
            internal uint Support;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Format
        {
            internal ushort Tag, Channels;
            internal uint SampleRate, BytesPerSecond;
            internal ushort BlockAlign, BitsPerSample, Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Header
        {
            internal IntPtr Data;
            internal uint Length, Recorded;
            internal IntPtr User;
            internal uint Flags, Loops;
            internal IntPtr Next, Reserved;
        }

        [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveOutGetDevCapsW(UIntPtr device, ref Caps caps, int size);
        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr handle, UIntPtr device, ref Format format, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr handle);

        /// <summary>Every playback device's name (Windows cuts names to 31 characters).</summary>
        internal static List<string> Devices()
        {
            var names = new List<string>();
            if (!OperatingSystem.IsWindows()) return names;
            uint count = waveOutGetNumDevs();
            for (uint i = 0; i < count; i++)
            {
                var caps = new Caps();
                if (waveOutGetDevCapsW((UIntPtr)i, ref caps, Marshal.SizeOf<Caps>()) == Success) names.Add(caps.Name);
            }
            return names;
        }

        /// <summary>
        /// Plays samples on the first device whose name contains device (any case), and returns when they have played
        /// or cancel fires. started is called the moment playback is handed to Windows.
        /// </summary>
        internal static void Play(string device, short[] samples, int sampleRate, Action started, CancellationToken cancel)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Playing into VRChat needs Windows.");
            List<string> devices = Devices();
            int index = devices.FindIndex(name => name.IndexOf(device, StringComparison.OrdinalIgnoreCase) >= 0);
            if (index < 0)
                throw new InvalidOperationException("No playback device named like \"" + device + "\" (found: " + (devices.Count == 0 ? "none" : string.Join(", ", devices)) +
                                                    "). Install a virtual audio cable, set VRChat's microphone to its output, and pass its input with --cable.");
            var format = new Format { Tag = 1, Channels = 1, SampleRate = (uint)sampleRate, BytesPerSecond = (uint)sampleRate * 2, BlockAlign = 2, BitsPerSample = 16 };
            int result = waveOutOpen(out IntPtr handle, (UIntPtr)(uint)index, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (result != Success) throw new InvalidOperationException("\"" + devices[index] + "\" could not be opened (error " + result + ").");
            int bytes = samples.Length * 2;
            IntPtr data = Marshal.AllocHGlobal(Math.Max(bytes, 2));
            IntPtr header = Marshal.AllocHGlobal(Marshal.SizeOf<Header>());
            try
            {
                Marshal.Copy(samples, 0, data, samples.Length);
                Marshal.StructureToPtr(new Header { Data = data, Length = (uint)bytes }, header, false);
                int size = Marshal.SizeOf<Header>();
                waveOutPrepareHeader(handle, header, size);
                started?.Invoke();
                waveOutWrite(handle, header, size);
                while ((Marshal.PtrToStructure<Header>(header).Flags & Done) == 0)
                {
                    if (cancel.WaitHandle.WaitOne(20)) break;
                }
                waveOutReset(handle);
                waveOutUnprepareHeader(handle, header, size);
            }
            finally
            {
                waveOutClose(handle);
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
            }
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder wave out
// ============================================================================
//
// FTRecorderWaveOut (this file)  Windows playback through winmm.
//   Devices()                Playback device names (31 characters at most; empty off Windows).
//   Play(device, samples, sampleRate, started, cancel)  Mono 16-bit PCM to the first device whose name contains
//                            device; started fires as playback is handed over; returns when done or cancelled.
