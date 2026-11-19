using System;
using System.Collections.Generic;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Visemes for a whole audio track: OVRLipSync when a library path is given and it loads, otherwise
    /// FTRecorderVisemeClassifier. Says which one it used.
    /// </summary>
    internal static class FTRecorderVisemeGenerator
    {
        internal const string Classifier = "FTRecorder classifier";
        internal const string OvrLipSync = "OVRLipSync";

        /// <summary>
        /// One frame per 10 ms. engine is the one used; note says why OVRLipSync was not, when a path was given and it
        /// failed (the classifier is then used instead).
        /// </summary>
        internal static List<FTRecorderVisemeFrame> Generate(short[] samples, string ovrLipSyncPath, out string engine, out string note)
        {
            note = null;
            if (!string.IsNullOrWhiteSpace(ovrLipSyncPath))
            {
                try
                {
                    using FTRecorderOvrLipSync lipSync = FTRecorderOvrLipSync.Load(ovrLipSyncPath);
                    engine = OvrLipSync;
                    return WithOvrLipSync(lipSync, samples);
                }
                catch (Exception exception) when (!(exception is OutOfMemoryException))
                {
                    note = "OVRLipSync was not used: " + exception.Message;
                }
            }
            engine = Classifier;
            return new FTRecorderVisemeClassifier().Classify(samples);
        }

        private static List<FTRecorderVisemeFrame> WithOvrLipSync(FTRecorderOvrLipSync lipSync, short[] samples)
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
    }
}

// ============================================================================
// GLOSSARY: FTRecorder viseme generator
// ============================================================================
//
// FTRecorderVisemeGenerator (this file)
//   Generate(samples, ovrLipSyncPath, out engine, out note)  OVRLipSync when the path is set and loads, else the
//                            classifier (note says why OVRLipSync was skipped). One frame per 10 ms.
//   Classifier, OvrLipSync   The engine names given out.
//   Voice with OVRLipSync    -55 dBFS is 0, -25 dBFS is 1, per 10 ms.
