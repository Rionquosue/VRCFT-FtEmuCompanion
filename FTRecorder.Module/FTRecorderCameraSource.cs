using System;
using System.Collections.Generic;
using System.IO;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// A webcam or a video file through ffmpeg and the face models (MediaPipe, and Babble when configured), live. A
    /// file plays at its own speed and loops when asked; a webcam that drops out is opened again after two seconds.
    /// When no face is seen the last face is held.
    /// </summary>
    internal sealed class FTRecorderCameraSource : FTRecorderSource
    {
        private readonly FTRecorderModuleConfig config;
        private readonly bool webcam;
        private volatile FTRecorderFfmpeg ffmpeg;

        internal FTRecorderCameraSource(FTRecorderModuleConfig config, bool webcam)
        {
            this.config = config;
            this.webcam = webcam;
        }

        internal override bool ProvidesEyes => true;

        protected override void Run()
        {
            string input;
            var options = new List<string>();
            if (webcam)
            {
                if (string.IsNullOrWhiteSpace(config.WebcamDevice))
                {
                    Status = "Set webcamDevice (ffmpeg -list_devices true -f dshow -i dummy lists cameras on Windows).";
                    return;
                }
                options.Add("-f");
                options.Add(config.WebcamFormat);
                input = config.WebcamFormat == "dshow" && !config.WebcamDevice.StartsWith("video=", StringComparison.Ordinal) ? "video=" + config.WebcamDevice : config.WebcamDevice;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(config.VideoFile) || !File.Exists(config.VideoFile))
                {
                    Status = "No video at \"" + config.VideoFile + "\": set videoFile.";
                    return;
                }
                input = config.VideoFile;
            }
            // Said at once, before the models load, when ffmpeg is missing.
            FTRecorderFfmpeg.Check(config.FfmpegPath);
            int size = Math.Clamp(config.FrameSize, 128, 1920);
            using var tracker = new FTRecorderFaceTracker(config.BabbleModel, config.BabbleLabels, config.BabbleCropScale, config.Threads);
            var frame = new FTRecorderFrame(size, size);
            var unified = new float[FTRecorderExpressionMap.UnifiedNames.Length];
            var eye = NeutralEyes();
            string models = "MediaPipe" + (tracker.UsesBabble ? " + Babble" : "");
            while (!Stopping)
            {
                int frames = 0;
                tracker.Reset();
                using (FTRecorderFfmpeg reader = FTRecorderFfmpeg.OpenVideo(config.FfmpegPath, options, input, size, config.Fps, !webcam))
                {
                    ffmpeg = reader;
                    Status = (webcam ? "Webcam " : "Video ") + "running (" + models + ")";
                    while (!Stopping && reader.ReadFrame(frame))
                    {
                        frames++;
                        if (tracker.TryTrack(frame, unified, eye)) Publish(unified, eye);
                    }
                    ffmpeg = null;
                    if (Stopping) return;
                    if (frames == 0) Status = "ffmpeg gave no frames: " + reader.Errors;
                }
                if (!webcam && !config.Loop)
                {
                    if (frames > 0) Status = "Video finished";
                    return;
                }
                if (!Wait(webcam || frames == 0 ? 2000 : 0)) return;
            }
        }

        protected override void Interrupt()
        {
            // Stopping ffmpeg ends the blocking frame read.
            ffmpeg?.Dispose();
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder camera source
// ============================================================================
//
// FTRecorderCameraSource (this file)  webcam (ffmpeg -f webcamFormat; dshow devices get "video=") or videoFile (at
//                            its own speed, -re), at frameSize and fps, through FTRecorderFaceTracker.
//   Run()                    Holds the last face when none is seen; reopens a webcam (or a failed file) after 2 s;
//                            loops a file when loop is set.
//   Interrupt()              Stops ffmpeg so a blocked read returns.
