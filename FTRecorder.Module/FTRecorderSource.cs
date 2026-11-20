using System;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Where the module's face data comes from. Each source works on its own thread and publishes whole frames; the
    /// module reads the newest one on VRCFaceTracking's update. Faults are kept as Status, never thrown at the module.
    /// </summary>
    internal abstract class FTRecorderSource : IDisposable
    {
        private readonly object gate = new object();
        private readonly float[] unified = new float[FTRecorderExpressionMap.UnifiedNames.Length];
        private readonly float[] eye = NeutralEyes();
        private long sequence;
        private volatile string status = "Starting";
        private Thread thread;
        protected volatile bool Stopping;

        /// <summary>Whether this source gives eye data at all.</summary>
        internal abstract bool ProvidesEyes { get; }

        /// <summary>A short line on what the source is doing, or its last fault.</summary>
        internal string Status
        {
            get => status;
            set => status = value;
        }

        internal static float[] NeutralEyes()
        {
            var values = new float[FTRecorderExpressionMap.EyeNames.Length];
            values[FTRecorderExpressionMap.LeftOpenness] = 1f;
            values[FTRecorderExpressionMap.RightOpenness] = 1f;
            return values;
        }

        internal void Start()
        {
            thread = new Thread(Guarded) { IsBackground = true, Name = "RiryTools FTRecorder " + GetType().Name };
            thread.Start();
        }

        private void Guarded()
        {
            try
            {
                Run();
            }
            catch (Exception exception)
            {
                // The thread must not end without a trace.
                // A missing file (ffmpeg, a model) is said plainly; anything else with its type.
                if (!Stopping) Status = exception is System.IO.FileNotFoundException ? exception.Message : "Stopped: " + exception.GetType().Name + ": " + exception.Message;
            }
        }

        /// <summary>The source's work, on its own thread, until Stopping.</summary>
        protected abstract void Run();

        /// <summary>Makes a frame the newest one. Arrays are copied.</summary>
        protected void Publish(float[] newUnified, float[] newEye)
        {
            lock (gate)
            {
                Array.Copy(newUnified, unified, unified.Length);
                if (newEye != null) Array.Copy(newEye, eye, eye.Length);
                sequence++;
            }
        }

        /// <summary>Copies the newest frame when it is newer than seen. False when nothing new has come.</summary>
        internal bool TryRead(ref long seen, float[] intoUnified, float[] intoEye)
        {
            lock (gate)
            {
                if (sequence == seen) return false;
                Array.Copy(unified, intoUnified, unified.Length);
                Array.Copy(eye, intoEye, eye.Length);
                seen = sequence;
                return true;
            }
        }

        /// <summary>Waits up to milliseconds, returning early when stopping. False once stopping.</summary>
        protected bool Wait(int milliseconds)
        {
            for (int waited = 0; waited < milliseconds && !Stopping; waited += 50) Thread.Sleep(Math.Min(50, milliseconds - waited));
            return !Stopping;
        }

        /// <summary>Called on Dispose, before the thread is joined: close sockets and processes that block it.</summary>
        protected virtual void Interrupt()
        {
        }

        public void Dispose()
        {
            Stopping = true;
            Interrupt();
            thread?.Join(3000);
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder sources
// ============================================================================
//
// FTRecorderSource (this file)  Base of the module's sources: one thread each, newest frame kept.
//   Start()                  Runs Run() on a background thread; an exception ends it with Status set (a missing file,
//                            such as ffmpeg, by its message alone).
//   Publish(unified, eye)    The newest frame (copied; eye null keeps the last eyes).
//   TryRead(ref seen, unified, eye)  Copies the newest frame when its sequence differs from seen.
//   Status                   What the source is doing or its last fault.
//   ProvidesEyes             False for sources without eye data (Babble OSC).
//   Wait(ms), Stopping, Interrupt()  For subclasses; Dispose sets Stopping, calls Interrupt, joins 3 s.
//   NeutralEyes()            Gaze 0, openness 1.
