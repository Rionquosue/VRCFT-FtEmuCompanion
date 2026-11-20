using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The viseme subtitle track of an .mkv recording, as SRT: one caption each time the viseme data changes, lasting
    /// until the next. A caption holds everything known at that moment: the viseme's index and name, Voice, and the 15
    /// weights when OVRLipSync made them (VRChat sends only Viseme and Voice).
    /// </summary>
    internal static class FTRecorderCaptions
    {
        internal static readonly string[] Names = { "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou" };
        private const double LastLength = 0.5;

        /// <summary>SRT text for the recording's visemes; null when it has none.</summary>
        internal static string Build(FTRecorderRecording recording)
        {
            var moments = new List<(double time, string text)>();
            int viseme = -1;
            float voice = 0;
            foreach (FTRecorderEntry entry in recording.Entries)
            {
                if (entry.Kind == FTRecorderEntryKind.Viseme)
                {
                    moments.Add((entry.Time, Text(entry.Viseme, entry.Voice, entry.Weights)));
                }
                else if (entry.Kind == FTRecorderEntryKind.Parameter && (entry.Name == "Viseme" || entry.Name == "Voice"))
                {
                    if (entry.Name == "Viseme") viseme = (int)entry.Value;
                    else voice = entry.Value;
                    moments.Add((entry.Time, Text(viseme, voice, null)));
                }
            }
            if (moments.Count == 0) return null;
            moments.Sort((a, b) => a.time.CompareTo(b.time));
            var srt = new StringBuilder();
            int number = 0;
            for (int i = 0; i < moments.Count; i++)
            {
                double start = moments[i].time;
                double end = i + 1 < moments.Count ? moments[i + 1].time : start + LastLength;
                // Values that arrive together (Viseme and Voice in one packet) make one caption: the last one.
                if (end <= start) continue;
                srt.Append(++number).Append('\n').Append(Clock(start)).Append(" --> ").Append(Clock(end)).Append('\n').Append(moments[i].text).Append("\n\n");
            }
            return srt.ToString();
        }

        private static string Text(int viseme, float voice, float[] weights)
        {
            var text = new StringBuilder();
            text.Append(viseme >= 0 ? viseme + " " + (viseme < Names.Length ? Names[viseme] : "?") : "-").Append("  voice ").Append(voice.ToString("0.00", CultureInfo.InvariantCulture));
            if (weights != null && weights.Length > 0)
            {
                text.Append('\n');
                for (int k = 0; k < weights.Length && k < Names.Length; k++)
                {
                    if (k > 0) text.Append(' ');
                    text.Append(Names[k]).Append(' ').Append(weights[k].ToString("0.00", CultureInfo.InvariantCulture));
                }
            }
            return text.ToString();
        }

        private static string Clock(double seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ((int)time.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + time.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   time.Seconds.ToString("00", CultureInfo.InvariantCulture) + "," + time.Milliseconds.ToString("000", CultureInfo.InvariantCulture);
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder captions
// ============================================================================
//
// FTRecorderCaptions (this file)
//   Build(recording)         SRT: a caption per change (each Viseme entry; each VRChat Viseme or Voice value), until the
//                            next change (the last lasts 0.5 s). Line 1: "<index> <name>  voice <0.00>"; line 2 (OVRLipSync
//                            only): the 15 weights by name. Null when there are no visemes.
//   Names                    VRChat's viseme order: sil PP FF TH DD kk CH SS nn RR aa E ih oh ou.
