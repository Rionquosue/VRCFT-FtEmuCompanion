using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    internal sealed class FTRecorderVideoOptions
    {
        internal string Video;
        internal string Output;
        internal string Ffmpeg;
        /// <summary>A Babble ONNX model for the mouth; null for MediaPipe only.</summary>
        internal string BabbleModel;
        internal string BabbleLabels;
        internal float BabbleCropScale = 1f;
        /// <summary>An OVRLipSync library; null to use VRChat's copy.</summary>
        internal string OvrLipSync;
        /// <summary>Use the FTRecorder classifier instead of OVRLipSync.</summary>
        internal bool Classifier;
        /// <summary>Asked for an OVRLipSync path, with the reason, when none loads; returns null or empty to give up.</summary>
        internal Func<string, string> AskForOvrLipSync;
        /// <summary>Frames per second taken from the video.</summary>
        internal double Fps = 30;
        /// <summary>The side of the square frames are fitted into, in pixels.</summary>
        internal int Size = 640;
        internal int Threads = 2;
    }

    /// <summary>
    /// A recorded video into a recording: every frame through the face models (MediaPipe, and Babble when given) as
    /// Unified Expressions and eyes, and the sound track through the viseme engine, in one file in time order. Frames with
    /// no face are left out, so playback holds the last face.
    /// </summary>
    internal static class FTRecorderVideoProcessor
    {
        /// <summary>Writes options.Output (it must not exist). progress gets short status lines. Returns a summary.</summary>
        internal static string Process(FTRecorderVideoOptions options, Action<string> progress, CancellationToken cancel)
        {
            if (!File.Exists(options.Video)) throw new FileNotFoundException("The video was not found.", options.Video);
            if (File.Exists(options.Output)) throw new IOException("The output already exists: " + options.Output);

            // OVRLipSync is found (or asked for) before any work, so a missing library stops the run at once.
            FTRecorderOvrLipSync lipSync = null;
            string engine = FTRecorderVisemeGenerator.Classifier;
            if (!options.Classifier)
            {
                lipSync = FTRecorderVisemeGenerator.Open(options.OvrLipSync, options.AskForOvrLipSync, out string loaded);
                engine = FTRecorderVisemeGenerator.OvrLipSync;
                progress?.Invoke("OVRLipSync: " + loaded);
            }
            List<FTRecorderVisemeFrame> visemes = new List<FTRecorderVisemeFrame>();
            using (lipSync)
            {
                progress?.Invoke("Reading audio");
                short[] audio = FTRecorderFfmpeg.ReadAudio(options.Ffmpeg, options.Video, cancel);
                if (audio.Length == 0) engine = "none (no audio)";
                else
                {
                    progress?.Invoke("Making visemes");
                    visemes = lipSync != null ? FTRecorderVisemeGenerator.WithOvrLipSync(lipSync, audio) : FTRecorderVisemeGenerator.WithClassifier(audio);
                }
            }

            using var tracker = new FTRecorderFaceTracker(options.BabbleModel, options.BabbleLabels, options.BabbleCropScale, options.Threads);
            string source = "video:" + Path.GetFileName(options.Video) + "; face: MediaPipe" + (tracker.UsesBabble ? " + Babble" : "") + "; visemes: " + engine;
            int frames = 0, faces = 0, written = 0;
            using (var writer = new FTRecorderRecordingWriter(options.Output, source))
            using (FTRecorderFfmpeg video = FTRecorderFfmpeg.OpenVideo(options.Ffmpeg, Array.Empty<string>(), options.Video, options.Size, options.Fps, false))
            {
                var frame = new FTRecorderFrame(options.Size, options.Size);
                var unified = new float[FTRecorderExpressionMap.UnifiedNames.Length];
                var eye = new float[FTRecorderExpressionMap.EyeNames.Length];
                int next = 0;
                while (video.ReadFrame(frame))
                {
                    cancel.ThrowIfCancellationRequested();
                    double time = frames / options.Fps;
                    for (; next < visemes.Count && visemes[next].Time <= time; next++) WriteViseme(writer, visemes[next]);
                    if (tracker.TryTrack(frame, unified, eye))
                    {
                        writer.WriteExpressions(time, unified, eye);
                        faces++;
                    }
                    frames++;
                    if (frames % 150 == 0) progress?.Invoke("Frame " + frames + " (" + time.ToString("0.0") + " s), face in " + faces);
                }
                for (; next < visemes.Count; next++) WriteViseme(writer, visemes[next]);
                written = visemes.Count;
                if (frames == 0) throw new InvalidDataException("ffmpeg gave no video frames. " + video.Errors);
            }
            return frames + " frames, a face in " + faces + "; " + written + " viseme frames (" + engine + ").";
        }

        private static void WriteViseme(FTRecorderRecordingWriter writer, FTRecorderVisemeFrame frame)
        {
            writer.WriteViseme(frame.Time, frame.Viseme, frame.Voice, frame.Weights);
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder video processor
// ============================================================================
//
// FTRecorderVideoOptions (this file)  Video, Output, Ffmpeg, BabbleModel, BabbleLabels, BabbleCropScale, OvrLipSync
//                            (null: VRChat's copy), Classifier, AskForOvrLipSync, Fps (30), Size (640), Threads (2).
// FTRecorderVideoProcessor
//   Process(options, progress, cancel)  OVRLipSync opened first (FTRecorderVisemeGenerator.Open) unless Classifier;
//                            then audio (16 kHz mono, visemes), then each
//                            frame through FTRecorderFaceTracker; one recording with expressions and visemes in time
//                            order. Frames with no face are left out. The source line names the models and engine.
