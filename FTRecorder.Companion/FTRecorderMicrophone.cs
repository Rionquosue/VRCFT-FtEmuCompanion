using System;
using System.IO;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The microphone during a recording: ffmpeg's 16 kHz mono 16-bit samples, 10 ms at a time, appended raw to a working
    /// file (so a crash keeps them) and, when OVRLipSync is given (VRChat not running), turned into Viseme entries (the
    /// strongest viseme, Voice and all 15 weights).
    /// </summary>
    internal sealed class FTRecorderMicrophone : IDisposable
    {
        internal const int SampleRate = FTRecorderVisemeClassifier.SampleRate;

        private readonly FTRecorderOvrLipSync lipSync;
        private readonly FTRecorderFfmpeg microphone;
        private readonly FTRecorderRecordingWriter writer;
        private readonly FileStream audio;
        private readonly Thread thread;
        private volatile bool stopping;
        private double audioStart = -1;

        internal long Blocks;

        /// <param name="lipSync">OVRLipSync for visemes, or null to keep only the audio.</param>
        internal FTRecorderMicrophone(FTRecorderFfmpeg microphone, string audioPath, FTRecorderOvrLipSync lipSync, FTRecorderRecordingWriter writer)
        {
            this.microphone = microphone;
            this.lipSync = lipSync;
            this.writer = writer;
            audio = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            thread = new Thread(Loop) { IsBackground = true, Name = "RiryTools FTRecorder microphone" };
            thread.Start();
        }

        /// <summary>When the first sample was heard, in the writer's seconds; -1 before any.</summary>
        internal double AudioStart => Volatile.Read(ref audioStart);

        private void Loop()
        {
            int hop = FTRecorderVisemeClassifier.Hop;
            var bytes = new byte[hop * 2];
            var samples = new short[hop];
            while (!stopping && microphone.ReadExactly(bytes))
            {
                // The block just read began 10 ms ago.
                if (audioStart < 0) Volatile.Write(ref audioStart, Math.Max(0, writer.Now - hop / (double)SampleRate));
                audio.Write(bytes, 0, bytes.Length);
                if (Interlocked.Increment(ref Blocks) % 100 == 0) audio.Flush();
                if (lipSync == null) continue;
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                float[] weights;
                try
                {
                    weights = lipSync.Process(samples, 0, hop);
                }
                catch (InvalidOperationException exception)
                {
                    if (!stopping) Console.Error.WriteLine("Visemes stopped: " + exception.Message);
                    continue;
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
            }
            if (!stopping) Console.Error.WriteLine("The microphone stopped: " + microphone.Errors);
        }

        public void Dispose()
        {
            stopping = true;
            microphone.Dispose();
            thread.Join(2000);
            audio.Dispose();
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder microphone
// ============================================================================
//
// FTRecorderMicrophone (this file)  Microphone (16 kHz mono s16 via ffmpeg) during record.
//   (microphone, audioPath, lipSync, writer)  Raw samples appended to audioPath (flushed every second); with lipSync,
//                            each 10 ms block through OVRLipSync into a Viseme entry (strongest, Voice -55 to -25 dBFS
//                            as 0 to 1, 15 weights).
//   AudioStart               The writer's time of the first sample (-1 before any): the audio's offset in the .mkv.
//   Blocks                   10 ms blocks read. Dispose stops ffmpeg, then the thread, then closes the file.
