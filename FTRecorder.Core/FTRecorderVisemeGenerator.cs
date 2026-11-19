using System;
using System.Collections.Generic;
using System.IO;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Visemes for a whole audio track, with OVRLipSync (VRChat's own engine) or, only when asked for, the FTRecorder
    /// classifier. OVRLipSync is the path given, else the copy in an installed VRChat; when neither loads, the caller is
    /// told why and asked for a path, until one loads or none is given.
    /// </summary>
    internal static class FTRecorderVisemeGenerator
    {
        internal const string Classifier = "FTRecorder classifier";
        internal const string OvrLipSync = "OVRLipSync";

        /// <summary>
        /// Opens OVRLipSync: path when given, else VRChat's copy. When that fails and ask is set, ask gets the reason
        /// and returns another path (null or empty to give up). Throws, with the reason, when nothing loads.
        /// </summary>
        internal static FTRecorderOvrLipSync Open(string path, Func<string, string> ask, out string loaded)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                path = FTRecorderVrchatInstall.FindOvrLipSync(out List<string> searched);
                if (path == null)
                {
                    string where = searched.Count == 0 ? "no VRChat install was found through Steam" : "none in " + string.Join(", ", searched);
                    path = Ask(ask, "OVRLipSync.dll was not found in an installed VRChat (" + where + ").");
                }
            }
            while (true)
            {
                try
                {
                    FTRecorderOvrLipSync lipSync = FTRecorderOvrLipSync.Load(path);
                    loaded = path;
                    return lipSync;
                }
                catch (Exception exception) when (exception is IOException || exception is BadImageFormatException || exception is DllNotFoundException ||
                                                  exception is EntryPointNotFoundException || exception is InvalidOperationException || exception is ArgumentException)
                {
                    path = Ask(ask, "OVRLipSync could not be loaded from \"" + path + "\": " + exception.Message);
                }
            }
        }

        private static string Ask(Func<string, string> ask, string reason)
        {
            const string Hint = " Give the path with --ovrlipsync, or use --visemes classifier.";
            if (ask == null) throw new FileNotFoundException(reason + Hint);
            string answer = ask(reason);
            // The reason was already shown with the question.
            if (string.IsNullOrWhiteSpace(answer)) throw new FileNotFoundException("No OVRLipSync path was given." + Hint);
            return answer.Trim().Trim('"');
        }

        /// <summary>One frame per 10 ms from OVRLipSync.</summary>
        internal static List<FTRecorderVisemeFrame> WithOvrLipSync(FTRecorderOvrLipSync lipSync, short[] samples)
        {
            int hop = FTRecorderVisemeClassifier.Hop;
            var frames = new List<FTRecorderVisemeFrame>(samples.Length / hop + 1);
            for (int start = 0; start + hop <= samples.Length; start += hop)
            {
                float[] weights = lipSync.Process(samples, start, hop);
                int best = 0;
                for (int k = 1; k < weights.Length; k++)
                {
                    if (weights[k] > weights[best]) best = k;
                }
                double energy = 0;
                for (int i = start; i < start + hop; i++) energy += (double)samples[i] * samples[i];
                double db = 10 * Math.Log10(energy / hop / (32768.0 * 32768.0) + 1e-12);
                // Voice as VRChat shows it is a loudness: here -55 dBFS is 0 and -25 dBFS is 1.
                float voice = (float)Math.Clamp((db + 55) / 30, 0, 1);
                frames.Add(new FTRecorderVisemeFrame { Time = (start + hop) / (double)FTRecorderVisemeClassifier.SampleRate, Weights = weights, Viseme = best, Voice = voice });
            }
            return frames;
        }

        /// <summary>One frame per 10 ms from the FTRecorder classifier.</summary>
        internal static List<FTRecorderVisemeFrame> WithClassifier(short[] samples) => new FTRecorderVisemeClassifier().Classify(samples);
    }
}

// ============================================================================
// GLOSSARY: FTRecorder viseme generator
// ============================================================================
//
// FTRecorderVisemeGenerator (this file)
//   Open(path, ask, out loaded)  OVRLipSync from path, else from VRChat (FTRecorderVrchatInstall.FindOvrLipSync). On
//                            failure ask(reason) gives another path; no ask, or an empty answer, throws FileNotFoundException.
//   WithOvrLipSync(lipSync, samples)  One frame per 10 ms; Voice: -55 dBFS is 0, -25 dBFS is 1.
//   WithClassifier(samples)  FTRecorderVisemeClassifier, used only when asked for (--visemes classifier).
//   Classifier, OvrLipSync   The engine names given out.
