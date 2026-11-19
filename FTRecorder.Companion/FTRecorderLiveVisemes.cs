using System;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The emulation for live recording when VRChat is not running: the microphone through ffmpeg, 10 ms at a time,
    /// into OVRLipSync, each result written as a Viseme entry (the strongest viseme, Voice and all 15 weights).
    /// </summary>
    internal sealed class FTRecorderLiveVisemes : IDisposable
    {
        private readonly FTRecorderOvrLipSync lipSync;
        private readonly FTRecorderFfmpeg microphone;
        private readonly FTRecorderRecordingWriter writer;
        private readonly Thread thread;
        private volatile bool stopping;

        internal long Frames;

        internal FTRecorderLiveVisemes(FTRecorderOvrLipSync lipSync, FTRecorderFfmpeg microphone, FTRecorderRecordingWriter writer)
        {
            this.lipSync = lipSync;
            this.microphone = microphone;
            this.writer = writer;
            thread = new Thread(Loop) { IsBackground = true, Name = "RiryTools FTRecorder live visemes" };
            thread.Start();
        }

        private void Loop()
        {
            int hop = FTRecorderVisemeClassifier.Hop;
            var bytes = new byte[hop * 2];
            var samples = new short[hop];
            while (!stopping && microphone.ReadExactly(bytes))
            {
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                float[] weights;
                try
                {
                    weights = lipSync.Process(samples, 0, hop);
                }
                catch (InvalidOperationException exception)
                {
                    if (!stopping) Console.Error.WriteLine("Visemes stopped: " + exception.Message);
                    return;
                }
                int best = 0;
                for (int k = 1; k < weights.Length; k++)
                {
                    if (weights[k] > weights[best]) best = k;
                }
                double energy = 0;
                foreach (short sample in samples) energy += (double)sample * sample;
                double db = 10 * Math.Log10(energy / hop / (32768.0 * 32768.0) + 1e-12);
                writer.WriteViseme(writer.Now, best, (float)Math.Clamp((db + 55) / 30, 0, 1), weights);
                Interlocked.Increment(ref Frames);
            }
            if (!stopping) Console.Error.WriteLine("The microphone stopped: " + microphone.Errors);
        }

        public void Dispose()
        {
            stopping = true;
            microphone.Dispose();
            thread.Join(2000);
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder live visemes
// ============================================================================
//
// FTRecorderLiveVisemes (this file)  Microphone (16 kHz mono via ffmpeg) to OVRLipSync every 10 ms; Viseme entries
//                            (strongest, Voice -55 dBFS to -25 dBFS as 0 to 1, 15 weights) timed by the writer. Frames
//                            counts them. Dispose stops ffmpeg, then the thread.
