using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Visemes for a video from VRChat itself: its sound is played into a virtual audio cable that VRChat uses as its
    /// microphone, and the Viseme and Voice VRChat sends back over OSC are kept, timed from the start of playback. Runs at
    /// the video's own speed.
    /// </summary>
    internal static class FTRecorderVrchatVisemes
    {
        private const int SampleRate = 48000;
        private const int LeadInSeconds = 5;

        internal static List<FTRecorderEntry> Capture(string ffmpeg, string video, string cable, Action<string> progress, CancellationToken cancel)
        {
            short[] audio = FTRecorderFfmpeg.ReadAudio(ffmpeg, video, cancel, SampleRate);
            var entries = new List<FTRecorderEntry>();
            if (audio.Length == 0) return entries;
            var gate = new object();
            var clock = new Stopwatch();
            using var listener = new FTRecorderVrchatListener(() => clock.IsRunning ? clock.Elapsed.TotalSeconds : -1, (time, name, type, value) =>
            {
                if (time < 0) return;
                lock (gate) entries.Add(new FTRecorderEntry { Time = time, Kind = FTRecorderEntryKind.Parameter, Name = name, Type = type, Value = value });
            });
            if (!listener.Discoverable) throw new InvalidOperationException("VRChat cannot find the viseme listener: multicast DNS is unavailable.");
            progress?.Invoke("Waiting " + LeadInSeconds + " s for VRChat to find the viseme listener");
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(LeadInSeconds))) cancel.ThrowIfCancellationRequested();
            progress?.Invoke("Playing " + (audio.Length / (double)SampleRate).ToString("0.0") + " s of audio into VRChat through \"" + cable + "\"");
            FTRecorderWaveOut.Play(cable, audio, SampleRate, clock.Start, cancel);
            // VRChat's last values arrive a moment after the sound ends.
            cancel.WaitHandle.WaitOne(500);
            cancel.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (entries.Count == 0) throw new InvalidOperationException("VRChat sent no visemes. Check that OSC is on, the microphone is the cable's output and not muted, and the avatar uses visemes.");
                entries.Sort((a, b) => a.Time.CompareTo(b.Time));
                return new List<FTRecorderEntry>(entries);
            }
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder VRChat visemes
// ============================================================================
//
// FTRecorderVrchatVisemes (this file)
//   Capture(ffmpeg, video, cable, progress, cancel)  The video's audio at 48 kHz mono played into the cable after a
//                            5 s lead-in (VRChat finding the listener); Viseme and Voice from FTRecorderVrchatListener,
//                            timed from playback start, as Parameter entries in time order. Throws when VRChat sent none.
