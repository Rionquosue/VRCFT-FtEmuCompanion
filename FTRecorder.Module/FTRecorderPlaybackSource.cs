using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Plays a recording's expression frames at their recorded times. Values are matched by name, so a recording made
    /// with a different list of Unified Expressions still plays what both know. Recordings of VRCFaceTracking's OSC
    /// output have no expression frames: those are replayed with the companion app instead.
    /// </summary>
    internal sealed class FTRecorderPlaybackSource : FTRecorderSource
    {
        private readonly string path;
        private readonly bool loop;
        private readonly string ffmpeg;
        private bool eyes = true;

        /// <param name="ffmpeg">For .mkv recordings, read through ffmpeg.</param>
        internal FTRecorderPlaybackSource(string path, bool loop, string ffmpeg)
        {
            this.path = path;
            this.loop = loop;
            this.ffmpeg = ffmpeg;
        }

        internal override bool ProvidesEyes => eyes;

        protected override void Run()
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Status = "No recording at \"" + path + "\": set playbackFile.";
                return;
            }
            FTRecorderRecording recording = FTRecorderRecording.Load(path, ffmpeg);
            var frames = new List<FTRecorderEntry>();
            foreach (FTRecorderEntry entry in recording.Entries)
            {
                if (entry.Kind == FTRecorderEntryKind.Expressions) frames.Add(entry);
            }
            if (frames.Count == 0)
            {
                Status = "The recording has no expression frames (an OSC recording is replayed with the companion app).";
                return;
            }
            frames.Sort((a, b) => a.Time.CompareTo(b.Time));
            int[] unifiedSlots = Slots(recording.Unified, FTRecorderExpressionMap.UnifiedNames);
            int[] eyeSlots = Slots(recording.Eye, FTRecorderExpressionMap.EyeNames);
            eyes = Array.Exists(eyeSlots, slot => slot >= 0);
            var unified = new float[FTRecorderExpressionMap.UnifiedNames.Length];
            var eye = NeutralEyes();
            double start = frames[0].Time, length = frames[frames.Count - 1].Time - start;
            Status = "Playing " + Path.GetFileName(path) + " (" + frames.Count + " frames, " + length.ToString("0.0") + " s)";
            do
            {
                var clock = Stopwatch.StartNew();
                foreach (FTRecorderEntry frame in frames)
                {
                    double due = frame.Time - start;
                    while (clock.Elapsed.TotalSeconds < due)
                    {
                        if (!Wait(Math.Max(1, (int)((due - clock.Elapsed.TotalSeconds) * 1000)))) return;
                    }
                    Copy(frame.Unified, unifiedSlots, unified);
                    Copy(frame.Eye, eyeSlots, eye);
                    Publish(unified, eye);
                }
            }
            while (loop && !Stopping);
            Status = "Finished " + Path.GetFileName(path);
        }

        // For each name the recording has, its slot in ours (-1 when we do not know it).
        private static int[] Slots(string[] recorded, string[] ours)
        {
            var slots = new int[recorded.Length];
            for (int i = 0; i < recorded.Length; i++) slots[i] = Array.IndexOf(ours, recorded[i]);
            return slots;
        }

        private static void Copy(float[] values, int[] slots, float[] into)
        {
            for (int i = 0; i < values.Length && i < slots.Length; i++)
            {
                if (slots[i] >= 0) into[slots[i]] = values[i];
            }
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder playback source
// ============================================================================
//
// FTRecorderPlaybackSource (this file)  A recording's expression frames (.ftrec.jsonl, or .mkv through ffmpeg) at their
//                            recorded times, matched by name;
//                            loops when asked. A recording without expression frames is reported, not played.
//   ProvidesEyes             False when the recording has none of the eye names.
